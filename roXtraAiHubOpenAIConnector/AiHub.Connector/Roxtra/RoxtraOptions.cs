using System.ComponentModel.DataAnnotations;

namespace AiHub.Connector.Roxtra;

public class RoxtraOptions
{
	public const string SectionName = "Roxtra";

	[Required]
	[Url]
	public string RoxtraUrl { get; set; } = string.Empty;
}
