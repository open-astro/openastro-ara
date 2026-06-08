using Newtonsoft.Json;

namespace OpenAstroAra.Equipment.Equipment.MyGuider.SkyGuard.SkyGuardMessages {
    /// <summary>
    /// Inclure l'url de la doc SkyGuard
    /// </summary>
    sealed class SkyGuardStatusMessage {
        [JsonProperty(PropertyName = "status")]
        public string Status { get; set; } = string.Empty;

        [JsonProperty(PropertyName = "data")]
        public string Data { get; set; } = string.Empty;

        [JsonProperty(PropertyName = "message")]
        public object? Message { get; set; }
    }
}