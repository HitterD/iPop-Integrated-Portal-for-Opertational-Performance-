using System.Text.Json;
using System.Text.Json.Serialization;
using IPop.Application.Projects;

namespace IPop.Web.Components.Projects;

/// Source-generated reader for the Gantt payload (the biggest JSON a project view loads). Same web defaults as HttpClient.GetFromJsonAsync.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GanttV2Dto))]
internal sealed partial class GanttJson : JsonSerializerContext;
