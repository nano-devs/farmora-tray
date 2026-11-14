using System.Text;
using FarmoraTray.Services;

namespace FarmoraTray.Tests;

public class DotMatrixPayloadTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n%...\n%%EOF");

    // ESC @, ESC x 0, "FAKTUR", CR LF, FF — what the backend's print.escp returns.
    private static readonly byte[] EscP = [0x1B, 0x40, 0x1B, 0x78, 0x00, .. "FAKTUR"u8, 0x0D, 0x0A, 0x0C];

    private static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/octet-stream")]
    [InlineData(null)]
    public void PdfBytes_ArePrintedAsPdf_WhateverTheContentType(string? contentType)
    {
        var result = DotMatrixPayload.Parse(contentType, Pdf);

        Assert.Equal(DotMatrixFormat.Pdf, result.Format);
        Assert.Equal(Pdf, result.Bytes);
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("application/vnd.epson.escp")]
    [InlineData(null)]
    public void EscPBytes_ArePrintedRaw(string? contentType)
    {
        var result = DotMatrixPayload.Parse(contentType, EscP);

        Assert.Equal(DotMatrixFormat.Raw, result.Format);
        Assert.Equal(EscP, result.Bytes);
    }

    [Fact]
    public void PdfBase64_IsPrintedAsPdf()
    {
        var result = DotMatrixPayload.Parse("application/json", Json($$"""{ "pdfBase64": "{{Convert.ToBase64String(Pdf)}}" }"""));

        Assert.Equal(DotMatrixFormat.Pdf, result.Format);
        Assert.Equal(Pdf, result.Bytes);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData(null)]
    public void RawBase64_IsPrintedRaw(string? contentType)
    {
        var result = DotMatrixPayload.Parse(contentType, Json($$"""{"rawBase64":"{{Convert.ToBase64String(EscP)}}"}"""));

        Assert.Equal(DotMatrixFormat.Raw, result.Format);
        Assert.Equal(EscP, result.Bytes);
    }

    [Fact]
    public void RawBase64_HoldingAPdf_IsStillPrintedAsPdf()
    {
        // Raw-printing PDF bytes would feed pages of garbage, so the bytes win.
        var result = DotMatrixPayload.Parse("application/json", Json($$"""{"rawBase64":"{{Convert.ToBase64String(Pdf)}}"}"""));

        Assert.Equal(DotMatrixFormat.Pdf, result.Format);
    }

    [Fact]
    public void PdfWithBomOrLeadingWhitespace_IsAPdf()
    {
        Assert.True(DotMatrixPayload.LooksLikePdf([0xEF, 0xBB, 0xBF, .. Pdf]));
        Assert.True(DotMatrixPayload.LooksLikePdf([(byte)'\n', (byte)' ', .. Pdf]));
        Assert.False(DotMatrixPayload.LooksLikePdf(EscP));
        Assert.False(DotMatrixPayload.LooksLikePdf("%PD"u8));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/pdf; charset=binary")]
    public void DeclaredPdf_ThatIsNotAPdf_IsRejected(string contentType)
    {
        var ex = Assert.Throws<InvalidDotMatrixPayloadException>(() => DotMatrixPayload.Parse(contentType, EscP));
        Assert.Contains("%PDF-", ex.Message);
    }

    [Fact]
    public void PdfBase64_ThatIsNotAPdf_IsRejected()
    {
        Assert.Throws<InvalidDotMatrixPayloadException>(() =>
            DotMatrixPayload.Parse("application/json", Json($$"""{"pdfBase64":"{{Convert.ToBase64String(EscP)}}"}""")));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"pdfBase64":""}""")]
    [InlineData("""{"pdfBase64":"JVBERi0=","rawBase64":"G0A="}""")]
    [InlineData("""{"rawBase64":"not base64!"}""")]
    [InlineData("""{"rawBase64":""")]
    public void BadJson_IsRejected(string json)
    {
        Assert.Throws<InvalidDotMatrixPayloadException>(() => DotMatrixPayload.Parse("application/json", Json(json)));
    }

    [Fact]
    public void EmptyBody_IsRejected()
    {
        Assert.Throws<InvalidDotMatrixPayloadException>(() => DotMatrixPayload.Parse("application/octet-stream", []));
    }

    [Fact]
    public void OctetStreamStartingWithABrace_IsNotParsedAsJson()
    {
        byte[] body = [(byte)'{', 0x1B, 0x40];

        var result = DotMatrixPayload.Parse("application/octet-stream", body);

        Assert.Equal(DotMatrixFormat.Raw, result.Format);
        Assert.Equal(body, result.Bytes);
    }
}
