namespace Khela.Common.Auth
{
    public class DeviceRegisterRequest
    {
        public string Fingerprint { get; set; } = string.Empty;
        public string AppSetId { get; set; } = string.Empty;
        public string GameVersion { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string TimeZone { get; set; } = string.Empty;
    }

    public class DeviceRegisterResponse
    {
        public string DeviceId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public int MatchScore { get; set; }
        public bool IsSameDevice { get; set; }
    }
}
