using System.Text.Json.Serialization;
using Quotalis.Core.Pricing;

namespace Quotalis.Core.Usage;

/// <summary>Source-generated JSON metadata, so Core works with reflection-based serialization disabled.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CodexFileState))]
[JsonSerializable(typeof(PricingOverrides))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Quotalis.Core.Settings.UiState))]
[JsonSerializable(typeof(Quotalis.Core.Plan.PlanUsage))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
