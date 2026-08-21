#include "infini_model_runtime.h"

#include <algorithm>
#include <cstring>
#include <fstream>
#include <memory>
#include <new>
#include <string>
#include <thread>
#include <vector>

#if defined(_MSC_VER)
#pragma warning(push, 0)
#endif
#include <ctranslate2/filesystem.h>
#include <ctranslate2/translator.h>
#include <sentencepiece_processor.h>
#if defined(_MSC_VER)
#pragma warning(pop)
#endif

namespace {

constexpr uint32_t kAbiVersion = 1;
constexpr size_t kMaximumInputBytes = 256 * 1024;
constexpr size_t kMaximumTargetTokenBytes = 32;
constexpr size_t kMaximumDecodingTokens = 512;

// CTranslate2 falls back to four computation threads whatever the machine offers, so a desktop CPU
// decodes a caption on a sixth of its cores while the rest idle and the pipeline's attempt deadline
// runs out. Half the logical processors leaves the concurrent capture, OCR and interface stages the
// other half; the upper bound reflects that one short sentence stops scaling well before that.
[[nodiscard]] size_t decoding_threads() noexcept {
    const size_t logical = std::thread::hardware_concurrency();
    if (logical == 0) {
        return 4;
    }
    return std::clamp<size_t>(logical / 2, 2, 16);
}

struct model_runtime {
    std::unique_ptr<sentencepiece::SentencePieceProcessor> tokenizer;
    ctranslate2::Translator translator;

    model_runtime(
        std::unique_ptr<sentencepiece::SentencePieceProcessor> tokenizer_value,
        const std::string& model_directory)
        : tokenizer(std::move(tokenizer_value)),
          translator(
              model_directory,
              ctranslate2::Device::CPU,
              ctranslate2::ComputeType::DEFAULT,
              {0},
              false,
              ctranslate2::ReplicaPoolConfig{decoding_threads(), 0, -1}) {
    }
};

// The captured text arrives as the lines the region showed, joined by line breaks. MADLAD-400 is a
// sentence-level model with no representation for one: given a multi-line block it degenerates into
// repeating a clause until the decoding cap, which costs minutes and returns the source language
// untranslated. Each line is therefore its own segment, and the segments decode as a single batch.
std::vector<std::string> split_lines(const char* text) {
    std::vector<std::string> lines;
    const std::string value(text);
    size_t start = 0;
    while (true) {
        const size_t separator = value.find('\n', start);
        const size_t stop = separator == std::string::npos ? value.size() : separator;
        size_t length = stop - start;
        if (length > 0 && value[start + length - 1] == '\r') {
            --length;
        }
        lines.emplace_back(value, start, length);
        if (separator == std::string::npos) {
            return lines;
        }
        start = separator + 1;
    }
}

bool has_content(const std::string& line) noexcept {
    return std::any_of(line.begin(), line.end(), [](const char character) noexcept {
        return static_cast<unsigned char>(character) > 0x20U;
    });
}

void write_error(char* destination, size_t capacity, const char* message) noexcept {
    if (destination == nullptr || capacity == 0) {
        return;
    }
    const size_t length = std::min(std::strlen(message), capacity - 1);
    std::memcpy(destination, message, length);
    destination[length] = '\0';
}

bool is_valid_target_token(const char* token) noexcept {
    if (token == nullptr) {
        return false;
    }
    const size_t length = std::strlen(token);
    if (length < 4 || length > kMaximumTargetTokenBytes ||
        token[0] != '<' || token[1] != '2' || token[length - 1] != '>') {
        return false;
    }
    for (size_t index = 2; index + 1 < length; ++index) {
        const char character = token[index];
        if (!((character >= 'a' && character <= 'z') ||
              (character >= 'A' && character <= 'Z') ||
              (character >= '0' && character <= '9') ||
              character == '-' || character == '_')) {
            return false;
        }
    }
    return true;
}

std::string read_file(const char* path) {
    std::ifstream input = ctranslate2::open_file_read(
        path,
        std::ios_base::in | std::ios_base::binary);
    input.seekg(0, std::ios::end);
    const std::streamoff length = input.tellg();
    if (length <= 0 || length > 64 * 1024 * 1024) {
        throw std::runtime_error("Tokenizer model size is invalid.");
    }
    input.seekg(0, std::ios::beg);
    std::string bytes(static_cast<size_t>(length), '\0');
    input.read(bytes.data(), length);
    if (!input) {
        throw std::runtime_error("Tokenizer model could not be read.");
    }
    return bytes;
}

}  // namespace

uint32_t INFINI_MODEL_CALL infini_model_runtime_abi_version(void) {
    return kAbiVersion;
}

int32_t INFINI_MODEL_CALL infini_model_runtime_create(
    const char* model_directory_utf8,
    const char* sentencepiece_model_utf8,
    void** runtime,
    char* error_utf8,
    size_t error_capacity) {
    if (runtime != nullptr) {
        *runtime = nullptr;
    }
    if (model_directory_utf8 == nullptr || sentencepiece_model_utf8 == nullptr ||
        runtime == nullptr || model_directory_utf8[0] == '\0' ||
        sentencepiece_model_utf8[0] == '\0') {
        write_error(error_utf8, error_capacity, "invalid runtime paths");
        return INFINI_MODEL_INVALID_ARGUMENT;
    }
    try {
        auto tokenizer =
            std::make_unique<sentencepiece::SentencePieceProcessor>();
        const std::string tokenizer_bytes = read_file(sentencepiece_model_utf8);
        const auto status = tokenizer->LoadFromSerializedProto(tokenizer_bytes);
        if (!status.ok()) {
            write_error(error_utf8, error_capacity, "tokenizer model is invalid");
            return INFINI_MODEL_LOAD_FAILED;
        }
        *runtime = new model_runtime(
            std::move(tokenizer),
            std::string(model_directory_utf8));
        return INFINI_MODEL_OK;
    } catch (const std::bad_alloc&) {
        write_error(error_utf8, error_capacity, "local model memory limit exceeded");
        return INFINI_MODEL_OUT_OF_MEMORY;
    } catch (const std::exception&) {
        write_error(error_utf8, error_capacity, "local model could not be loaded");
        return INFINI_MODEL_LOAD_FAILED;
    }
}

int32_t INFINI_MODEL_CALL infini_model_runtime_translate(
    void* runtime,
    const char* target_token_utf8,
    const char* source_text_utf8,
    size_t maximum_output_characters,
    char* output_utf8,
    size_t output_capacity,
    size_t* output_bytes,
    char* error_utf8,
    size_t error_capacity) {
    if (output_bytes != nullptr) {
        *output_bytes = 0;
    }
    if (runtime == nullptr || !is_valid_target_token(target_token_utf8) ||
        source_text_utf8 == nullptr || source_text_utf8[0] == '\0' ||
        maximum_output_characters == 0 || output_utf8 == nullptr ||
        output_capacity == 0 || output_bytes == nullptr ||
        std::strlen(source_text_utf8) > kMaximumInputBytes) {
        write_error(error_utf8, error_capacity, "invalid translation request");
        return INFINI_MODEL_INVALID_ARGUMENT;
    }
    try {
        auto* model = static_cast<model_runtime*>(runtime);
        const std::vector<std::string> lines = split_lines(source_text_utf8);
        std::vector<std::vector<std::string>> batch;
        std::vector<size_t> batch_lines;
        batch.reserve(lines.size());
        batch_lines.reserve(lines.size());
        for (size_t index = 0; index < lines.size(); ++index) {
            if (!has_content(lines[index])) {
                continue;
            }
            std::string input(target_token_utf8);
            input.push_back(' ');
            input.append(lines[index]);
            std::vector<std::string> source_tokens;
            const auto encode_status = model->tokenizer->Encode(input, &source_tokens);
            if (!encode_status.ok() || source_tokens.empty()) {
                write_error(error_utf8, error_capacity, "source tokenization failed");
                return INFINI_MODEL_TRANSLATION_FAILED;
            }
            if (source_tokens.back() != "</s>") {
                source_tokens.emplace_back("</s>");
            }
            batch.push_back(std::move(source_tokens));
            batch_lines.push_back(index);
        }
        if (batch.empty()) {
            write_error(error_utf8, error_capacity, "invalid translation request");
            return INFINI_MODEL_INVALID_ARGUMENT;
        }

        ctranslate2::TranslationOptions options;
        options.beam_size = 1;
        options.max_input_length = 1024;
        options.max_decoding_length = std::min(
            kMaximumDecodingTokens,
            maximum_output_characters);
        const auto results = model->translator.translate_batch(batch, options);
        if (results.size() != batch.size()) {
            write_error(error_utf8, error_capacity, "local model produced no translation");
            return INFINI_MODEL_TRANSLATION_FAILED;
        }

        // Blank source lines keep their place so the translated block stays aligned with the region.
        std::vector<std::string> translated_lines(lines.size());
        for (size_t index = 0; index < results.size(); ++index) {
            if (results[index].output().empty()) {
                write_error(error_utf8, error_capacity, "local model produced no translation");
                return INFINI_MODEL_TRANSLATION_FAILED;
            }
            std::vector<std::string> output_tokens;
            output_tokens.reserve(results[index].output().size());
            for (const std::string& token : results[index].output()) {
                if (token != "</s>" && token != "<pad>") {
                    output_tokens.push_back(token);
                }
            }
            std::string line;
            const auto decode_status = model->tokenizer->Decode(output_tokens, &line);
            if (!decode_status.ok() || line.empty()) {
                write_error(error_utf8, error_capacity, "result detokenization failed");
                return INFINI_MODEL_TRANSLATION_FAILED;
            }
            translated_lines[batch_lines[index]] = std::move(line);
        }

        std::string translated;
        for (size_t index = 0; index < translated_lines.size(); ++index) {
            if (index > 0) {
                translated.push_back('\n');
            }
            translated.append(translated_lines[index]);
        }
        if (translated.size() + 1 > output_capacity) {
            write_error(error_utf8, error_capacity, "translation output buffer is too small");
            return INFINI_MODEL_OUTPUT_TOO_LARGE;
        }
        std::memcpy(output_utf8, translated.data(), translated.size());
        output_utf8[translated.size()] = '\0';
        *output_bytes = translated.size();
        return INFINI_MODEL_OK;
    } catch (const std::bad_alloc&) {
        write_error(error_utf8, error_capacity, "local model memory limit exceeded");
        return INFINI_MODEL_OUT_OF_MEMORY;
    } catch (const std::exception&) {
        write_error(error_utf8, error_capacity, "local model translation failed");
        return INFINI_MODEL_TRANSLATION_FAILED;
    }
}

void INFINI_MODEL_CALL infini_model_runtime_destroy(void* runtime) {
    delete static_cast<model_runtime*>(runtime);
}
