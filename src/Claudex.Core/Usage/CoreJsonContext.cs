using System.Text.Json.Serialization;
using Claudex.Core.Pricing;

namespace Claudex.Core.Usage;

/// <summary>Source-generated JSON metadata, so Core works with reflection-based serialization disabled.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CodexFileState))]
[JsonSerializable(typeof(PricingOverrides))]
[JsonSerializable(typeof(Claudex.Core.Focus.FocusSessionState))]
[JsonSerializable(typeof(Claudex.Core.Awake.AwakeJournal))]
[JsonSerializable(typeof(List<Claudex.Core.Tasks.TaskItem>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Claudex.Core.Settings.UiState))]
[JsonSerializable(typeof(Claudex.Core.Plan.PlanUsage))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
