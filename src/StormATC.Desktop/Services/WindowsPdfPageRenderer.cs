using System.IO;
using StormATC.Application.Charts;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace StormATC.Desktop.Services;

/// <summary>
/// Draws PDF pages with the renderer Windows ships (<c>Windows.Data.Pdf</c>), so the charts in the
/// charts folder need no PDF software and no extra library.
/// </summary>
public sealed class WindowsPdfPageRenderer : IVacPageRenderer
{
    /// <inheritdoc />
    public async Task<int> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken)
    {
        (PdfDocument document, FileStream stream) = await OpenAsync(pdfPath, cancellationToken).ConfigureAwait(false);
        using (stream)
        {
            return (int)document.PageCount;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> RenderPageAsync(string pdfPath, int page, int width, CancellationToken cancellationToken)
    {
        (PdfDocument document, FileStream stream) = await OpenAsync(pdfPath, cancellationToken).ConfigureAwait(false);
        using (stream)
        {
            if (page < 1 || page > document.PageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(page));
            }

            using PdfPage pdfPage = document.GetPage((uint)(page - 1));
            using var output = new InMemoryRandomAccessStream();
            await pdfPage
                .RenderToStreamAsync(output, new PdfPageRenderOptions { DestinationWidth = (uint)width })
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            output.Seek(0);
            var png = new byte[output.Size];
            using DataReader reader = new(output);
            await reader.LoadAsync((uint)output.Size).AsTask(cancellationToken).ConfigureAwait(false);
            reader.ReadBytes(png);
            return png;
        }
    }

    private static async Task<(PdfDocument Document, FileStream Stream)> OpenAsync(string pdfPath, CancellationToken cancellationToken)
    {
        var stream = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            PdfDocument document = await PdfDocument
                .LoadFromStreamAsync(stream.AsRandomAccessStream())
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            return (document, stream);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
