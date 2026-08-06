namespace Immich.ToGPhoto.App
{
    public class AppConfig: SyncUserConfig
    {
        public required string HostGPMC { get; init; } = "http://localhost:2282";
        public required string HostImmich { get; init; }
        public List<SyncUserModel> SyncUserModels { get; init; } = [];
        public int RestartWaitSecond { get; init; } = 5 * 60;
    }

    public class SyncUserModel
    {
        public required string ImmichKey { get; init; }
        public required string GPhotoKey { get; init; }
    }

    public class SyncUserConfig
    {
        public int CountChankUpload { get; init; } = 30;
        public int CountThreadsGPMC { get; init; } = 1;
        public int CountThreadsImmich { get; init; } = 10;
        public int TakeUpload { get; init; } = int.MaxValue;
    }
}