using System.Globalization;
using System.Text;
namespace KuniWhatsThisAsset.Core;
// Emits USTAR headers for ordinary members and GNU long-name records when needed.
internal sealed class PackageTarWriter(Stream output)
{
    public void Write(string name, long length, Stream? data, bool directory, CancellationToken token)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(name);
        bool longName = encoded.Length > 100;
        if (longName)
        {
            byte[] value = [.. encoded, 0]; Header("././@LongLink", value.Length, (byte)'L', true); output.Write(value); Padding(value.Length);
        }
        Header(longName ? "long-member" : name, length, directory ? (byte)'5' : (byte)'0', longName);
        var buffer = new byte[65536]; long remaining = length;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested(); int n = data?.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)) ?? 0;
            if (n == 0) throw new EndOfStreamException("再梱包するメンバーが途中で切れています。"); output.Write(buffer, 0, n); remaining -= n;
        }
        Padding(length);
    }
    public void WriteBytes(string name, byte[] value, CancellationToken token) { using var input = new MemoryStream(value); Write(name, value.Length, input, false, token); }
    public void Complete() => output.Write(new byte[1024]);
    private void Padding(long size) { int count = (int)((512 - size % 512) % 512); if (count > 0) output.Write(new byte[count]); }
    private void Header(string name, long length, byte type, bool gnu)
    {
        byte[] bytes = new byte[512]; Encoding.UTF8.GetBytes(name).CopyTo(bytes, 0);
        Number(bytes.AsSpan(100, 8), type == '5' ? 493 : 420); Number(bytes.AsSpan(108, 8), 0); Number(bytes.AsSpan(116, 8), 0); Number(bytes.AsSpan(124, 12), length); Number(bytes.AsSpan(136, 12), 0);
        bytes[156] = type; (gnu ? "ustar "u8 : "ustar\0"u8).CopyTo(bytes.AsSpan(257));
        bytes[263] = gnu ? (byte)' ' : (byte)'0'; bytes[264] = gnu ? (byte)0 : (byte)'0';
        bytes.AsSpan(148, 8).Fill(32); string sum = Convert.ToString(bytes.Sum(b => (long)b), 8).PadLeft(6, '0'); Encoding.ASCII.GetBytes(sum).CopyTo(bytes, 148); bytes[154] = 0; bytes[155] = 32; output.Write(bytes);
    }
    private static void Number(Span<byte> field, long value)
    {
        string octal = Convert.ToString(value, 8);
        if (octal.Length < field.Length) { Encoding.ASCII.GetBytes(octal.PadLeft(field.Length - 1, '0')).CopyTo(field); field[^1] = 0; return; }
        for (int i = field.Length - 1; i >= 0; i--) { field[i] = (byte)value; value >>= 8; } field[0] |= 0x80;
    }
}
