using System.ComponentModel.DataAnnotations;

namespace AiHub.Connector.ExternalConnectors.OpenAiVectorStoreConnector;

[CustomValidation(typeof(OpenAiVectorStoreOptions), nameof(ValidateChunkConfig))]
public sealed class OpenAiVectorStoreOptions
{
	public const string SectionName = "OpenAI";

	[Required(AllowEmptyStrings = false)]
	public required string ApiKey { get; init; }

	/// <summary>Optional base URL for proxy/alternative endpoints. Must be HTTPS. Default: OpenAI production.</summary>
	[Url]
	public string? BaseUrl { get; init; }

	/// <summary>Prefix for vector store names (disambiguates environments).</summary>
	[StringLength(50, MinimumLength = 1)]
	public string VectorStoreNamePrefix { get; init; } = "roXtra-aihub";

	/// <summary>
	/// Chunking strategy: "auto" (default) or "static".
	/// Auto strategy uses OpenAI's latest chunking algorithm.
	/// </summary>
	[AllowedValues("auto", "static")]
	public string ChunkingStrategy { get; init; } = "auto";

	/// <summary>
	/// Maximum chunk size in tokens when using static chunking strategy.
	/// Default: 800. Max: 4096. Only used when ChunkingStrategy is "static".
	/// </summary>
	[Range(100, 4096)]
	public int MaxChunkSizeTokens { get; init; } = 800;

	/// <summary>
	/// Chunk overlap in tokens when using static chunking strategy.
	/// Default: 400. Must be less than MaxChunkSizeTokens. Only used when ChunkingStrategy is "static".
	/// </summary>
	[Range(0, 4096)]
	public int ChunkOverlapTokens { get; init; } = 400;

	public static ValidationResult? ValidateChunkConfig(OpenAiVectorStoreOptions opts, ValidationContext _)
	{
		if (string.Equals(opts.ChunkingStrategy, "static", StringComparison.OrdinalIgnoreCase) && opts.ChunkOverlapTokens >= opts.MaxChunkSizeTokens)
		{
			return new ValidationResult($"ChunkOverlapTokens ({opts.ChunkOverlapTokens}) must be less than MaxChunkSizeTokens ({opts.MaxChunkSizeTokens}).");
		}

		return ValidationResult.Success;
	}
}
