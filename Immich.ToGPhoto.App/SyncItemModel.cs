namespace Immich.ToGPhoto.App
{
    public class SyncItemModel
    {
        public int Id { get; set; }
        public required Guid ImmichKey { get; set; }
        public required string GoogleKey { get; set; }
    }
}
