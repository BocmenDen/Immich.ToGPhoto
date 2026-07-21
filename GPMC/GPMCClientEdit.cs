namespace GPMC
{
    public partial class GPMCClient
    {
        public string Key { get; init; }
        internal SemaphoreSlim LockDB = new(1, 1);
        private string _pathDB;

        public async ValueTask<string> GetPathDB(CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrWhiteSpace(_pathDB)) return _pathDB;
            _pathDB = (await GetDBPathAsync(cancellationToken)).Path;
            return _pathDB;
        }
    }
}