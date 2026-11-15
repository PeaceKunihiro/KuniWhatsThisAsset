using System.Text;
namespace KuniWhatsThisAsset.Core;

public static class TextDecoder
{
    static TextDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static string Decode(byte[] bytes, bool partial, out bool uncertain)
    {
        uncertain = false;
        Encoding? bom = null;
        int offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { bom = new UTF8Encoding(false, true); offset = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { bom = new UnicodeEncoding(false, false, true); offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { bom = new UnicodeEncoding(true, false, true); offset = 2; }
        string DecodeWith(Encoding encoding)
        {
            var decoder = encoding.GetDecoder();
            var chars = new char[encoding.GetMaxCharCount(bytes.Length - offset)];
            int count = decoder.GetChars(bytes, offset, bytes.Length - offset, chars, 0, flush: !partial);
            return new string(chars, 0, count);
        }
        if (bom != null)
        {
            try { return DecodeWith(bom); }
            catch (DecoderFallbackException) { uncertain = true; return Encoding.GetEncoding(bom.CodePage).GetString(bytes, offset, bytes.Length - offset); }
        }
        try { return DecodeWith(new UTF8Encoding(false, true)); }
        catch (DecoderFallbackException) { }
        try { return DecodeWith(Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)); }
        catch (DecoderFallbackException) { uncertain = true; return Encoding.UTF8.GetString(bytes); }
    }
}
