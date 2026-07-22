using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using InfiniTranseon.Contracts.Security;

namespace InfiniTranseon.Core.Updates;

public sealed class FileSignedSequenceState : ISignedSequenceState
{
    private sealed record StateDocument(int SchemaVersion, string Scope, long HighestAccepted);

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _scope;
    private readonly string _mutexName;
    private long _highestAccepted;

    public FileSignedSequenceState(string path, string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        _path = Path.GetFullPath(path);
        _scope = scope;
        _mutexName = "Local\\InfiniTranseon.Sequence." + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(_path.ToUpperInvariant() + "\n" + _scope)));
        _highestAccepted = Load();
    }

    public long HighestAccepted
    {
        get { lock (_gate) return _highestAccepted; }
    }

    public bool TryAccept(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        lock (_gate)
        {
            using var mutex = new Mutex(false, _mutexName);
            bool ownsMutex = false;
            try
            {
                try { ownsMutex = mutex.WaitOne(); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                long persisted = Load();
                if (persisted == 0 && _highestAccepted > 0)
                    throw new InvalidDataException(
                        "Sequence state disappeared; downgrade protection cannot continue.");
                _highestAccepted = Math.Max(_highestAccepted, persisted);
                if (sequence < _highestAccepted) return false;
                if (sequence == _highestAccepted) return true;
                Persist(sequence);
                _highestAccepted = sequence;
                return true;
            }
            finally
            {
                if (ownsMutex) mutex.ReleaseMutex();
            }
        }
    }

    private long Load()
    {
        if (!File.Exists(_path)) return 0;
        try
        {
            byte[] bytes = File.ReadAllBytes(_path);
            if (bytes.Length is < 2 or > 4096) throw new InvalidDataException("Sequence state size is invalid.");
            StateDocument document = JsonSerializer.Deserialize<StateDocument>(bytes) ??
                throw new InvalidDataException("Sequence state is empty.");
            if (document.SchemaVersion != 1 || document.Scope != _scope || document.HighestAccepted < 1)
                throw new InvalidDataException("Sequence state does not match its expected scope.");
            return document.HighestAccepted;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Sequence state is corrupted; downgrade protection cannot continue.", exception);
        }
    }

    private void Persist(long sequence)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("Sequence state path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = _path + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new StateDocument(1, _scope, sequence));
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
