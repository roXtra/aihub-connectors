using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace AiHub.Connector.ExternalConnectors.M365Connector;

public class PdfTextExtractor : IPdfTextExtractor
{
	private readonly ILogger<PdfTextExtractor> _logger;

	public PdfTextExtractor(ILogger<PdfTextExtractor> logger)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	public async Task<string> ExtractAsync(Stream pdfStream, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		if (pdfStream is null)
		{
			throw new ArgumentNullException(nameof(pdfStream));
		}

		_logger.LogDebug("Buffering PDF stream to temp file for parsing.");
		var tempPath = Path.Combine(Path.GetTempPath(), $"aihub_pdf_{Guid.NewGuid():N}.pdf");
		try
		{
			await using (var fsWrite = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, true))
			{
				await pdfStream.CopyToAsync(fsWrite, ct).ConfigureAwait(false);
			}
			using var fsRead = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
			using var doc = PdfDocument.Open(fsRead);
			var sb = new StringBuilder();
			foreach (var page in doc.GetPages())
			{
				_ = sb.AppendLine(page.Text);
			}
			if (sb.Length > 4 * 1024 * 1024)
			{
				_logger.LogError("Extracted text exceeds 4MB.");
				throw new InvalidOperationException("Extracted text exceeds maximum allowed size for external item content (4MB).");
			}
			return sb.ToString();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to extract text from PDF");
			throw;
		}
		finally
		{
			try
			{
				if (File.Exists(tempPath))
				{
					File.Delete(tempPath);
				}
			}
			catch
			{
				// best-effort cleanup
			}
		}
	}
}
