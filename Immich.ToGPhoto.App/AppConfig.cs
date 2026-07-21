namespace Immich.ToGPhoto.App
{
    public class AppConfig
    {
        public required string HostGPMC { get; init; } = "http://localhost:2282";
        public required string HostImmich { get; init; }
        public List<SyncUserModel> SyncUserModels { get; init; } = [];
        public TimeSpan Timer { get; init; } = TimeSpan.FromMinutes(30);
    }

    public class SyncUserModel
    {
        public required string ImmichKey { get; init; }
        public required string GPhotoKey { get; init; }
    }
}
