using System.Numerics;
using System.Security.Cryptography;

namespace InfiniTranseon.Core.Updates;

internal static class Ed25519Verifier
{
    private static readonly BigInteger Prime = (BigInteger.One << 255) - 19;
    private static readonly BigInteger GroupOrder = (BigInteger.One << 252) +
        BigInteger.Parse("27742317777372353535851937790883648493", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger SquareRootOfMinusOne = BigInteger.ModPow(2, (Prime - 1) / 4, Prime);
    private static readonly Point BasePoint = Point.FromAffine(
        BigInteger.Parse("15112221349535400772501151409588531511454012693041857206046113283949847762202",
            System.Globalization.CultureInfo.InvariantCulture),
        BigInteger.Parse("46316835694926478169428394003475163141307993866256225615783033603165251855960",
            System.Globalization.CultureInfo.InvariantCulture));
    private static readonly Point Identity = Point.FromAffine(BigInteger.Zero, BigInteger.One);

    public static bool Verify(
        ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> publicKey)
    {
        if (signature.Length != 64 || publicKey.Length != 32) return false;
        BigInteger scalar = DecodeInteger(signature[32..]);
        if (scalar >= GroupOrder) return false;
        if (!TryDecodePoint(publicKey, out Point publicPoint) ||
            !TryDecodePoint(signature[..32], out Point encodedR)) return false;
        if (Equal(Multiply(publicPoint, 8), Identity) || Equal(Multiply(encodedR, 8), Identity))
            return false;

        byte[] hashInput = new byte[64 + message.Length];
        signature[..32].CopyTo(hashInput);
        publicKey.CopyTo(hashInput.AsSpan(32));
        message.CopyTo(hashInput.AsSpan(64));
        byte[] digest = SHA512.HashData(hashInput);
        BigInteger challenge = DecodeInteger(digest) % GroupOrder;
        CryptographicOperations.ZeroMemory(hashInput);
        CryptographicOperations.ZeroMemory(digest);

        Point left = Multiply(Multiply(BasePoint, scalar), 8);
        Point right = Add(Multiply(encodedR, 8), Multiply(Multiply(publicPoint, challenge), 8));
        return Equal(left, right);
    }

    private static bool TryDecodePoint(ReadOnlySpan<byte> encoded, out Point point)
    {
        byte[] yBytes = encoded.ToArray();
        int sign = yBytes[31] >> 7;
        yBytes[31] &= 0x7f;
        BigInteger y = DecodeInteger(yBytes);
        CryptographicOperations.ZeroMemory(yBytes);
        if (y >= Prime)
        {
            point = default;
            return false;
        }
        BigInteger ySquared = Mod(y * y);
        BigInteger xSquared = Mod((ySquared - 1) * Inverse(Mod(D * ySquared + 1)));
        BigInteger x = BigInteger.ModPow(xSquared, (Prime + 3) / 8, Prime);
        if (Mod(x * x - xSquared) != BigInteger.Zero) x = Mod(x * SquareRootOfMinusOne);
        if (Mod(x * x - xSquared) != BigInteger.Zero)
        {
            point = default;
            return false;
        }
        if ((int)(x & 1) != sign) x = Prime - x;
        if (x.IsZero && sign != 0)
        {
            point = default;
            return false;
        }
        point = Point.FromAffine(x, y);
        return true;
    }

    private static Point Add(Point left, Point right)
    {
        BigInteger a = Mod((left.Y - left.X) * (right.Y - right.X));
        BigInteger b = Mod((left.Y + left.X) * (right.Y + right.X));
        BigInteger c = Mod(2 * D * left.T * right.T);
        BigInteger d = Mod(2 * left.Z * right.Z);
        BigInteger e = Mod(b - a);
        BigInteger f = Mod(d - c);
        BigInteger g = Mod(d + c);
        BigInteger h = Mod(b + a);
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Multiply(Point point, BigInteger scalar)
    {
        Point result = Identity;
        Point addend = point;
        while (scalar > 0)
        {
            if (!scalar.IsEven) result = Add(result, addend);
            addend = Add(addend, addend);
            scalar >>= 1;
        }
        return result;
    }

    private static bool Equal(Point left, Point right) =>
        Mod(left.X * right.Z - right.X * left.Z).IsZero &&
        Mod(left.Y * right.Z - right.Y * left.Z).IsZero;

    private static BigInteger DecodeInteger(ReadOnlySpan<byte> bytes) =>
        new(bytes, isUnsigned: true, isBigEndian: false);

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), Prime - 2, Prime);

    private static BigInteger Mod(BigInteger value)
    {
        BigInteger result = value % Prime;
        return result.Sign < 0 ? result + Prime : result;
    }

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T)
    {
        public static Point FromAffine(BigInteger x, BigInteger y) => new(x, y, BigInteger.One, Mod(x * y));
    }
}
