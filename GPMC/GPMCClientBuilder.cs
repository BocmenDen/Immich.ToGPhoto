namespace GPMC
{
    public class GPMCClientBuilder
    {
        private static readonly Dictionary<string, GPMCClient> _cache = [];

        public static GPMCClient Build(string host, string api)
        {
            var adress = new Uri(host);
            string key = $"{adress}_{api}";
            if (_cache.TryGetValue(key, out GPMCClient? cachedClient))
                return cachedClient;

            HttpClient httpClient = new()
            {
                BaseAddress = adress,
                Timeout = Timeout.InfiniteTimeSpan
            };
            httpClient.DefaultRequestHeaders.Add("auth_data", api);
            var client = new GPMCClient("/", httpClient) { Key = api };
            _cache[key] = client;
            return client;
        }
    }
}