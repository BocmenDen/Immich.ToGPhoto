using Dapper;
using System.Data.SQLite;

namespace GPMC
{
    public static class ClientExtensions
    {
        public static async Task<Dictionary<Uri, string>> UploadFilesAsync(this GPMCClient client, UploadRequest uploadRequest, CancellationToken cancellationToken = default)
        {
            var rawData = (await client.UploadFilesReturnMediaKeysAsync(uploadRequest, cancellationToken)).Files.ToDictionary(x => x.Value, x => x.Key);
            Dictionary<Uri, string> result = [];
            await foreach (var (mediaKey, dedupKey) in ConvertToDedupKey(client, rawData.Keys))
            {
                var path = rawData[mediaKey];
                result.Add(new(path), dedupKey);
            }
            return result;
        }

        public static async IAsyncEnumerable<string> GetKeys(this GPMCClient client)
        {
            await client.LockDB.WaitAsync();
            try
            {
                using var conn = await GetConnection(client);
                var cmd = new SQLiteCommand("SELECT dedup_key FROM remote_media", conn);
                using var reader = cmd.ExecuteReader();
                while (await reader.ReadAsync())
                {
                    var deduKey = reader["dedup_key"]?.ToString();
                    if (!string.IsNullOrEmpty(deduKey))
                        yield return deduKey;
                }
            }
            finally
            {
                client.LockDB.Release();
            }
        }

        public static async IAsyncEnumerable<string> IntersectKeys(this GPMCClient client, IEnumerable<string> keys)
        {
            await client.LockDB.WaitAsync();
            try
            {
                using var conn = await GetConnection(client);
                var query = "SELECT dedup_key FROM remote_media WHERE dedup_key IN @Keys";
                using var reader = await conn.ExecuteReaderAsync(query, new { Keys = keys });
                while (await reader.ReadAsync())
                {
                    var deduKey = reader["dedup_key"]?.ToString();
                    if (!string.IsNullOrEmpty(deduKey))
                        yield return deduKey;
                }
            }
            finally
            {
                client.LockDB.Release();
            }
        }

        public static async IAsyncEnumerable<(string media_key, string dedup_key)> ConvertToDedupKey(this GPMCClient client, IEnumerable<string> mediaKeys)
        {
            await client.LockDB.WaitAsync();
            try
            {
                using var conn = await GetConnection(client);
                var query = "SELECT media_key, dedup_key FROM remote_media WHERE media_key IN @Keys";
                using var reader = await conn.ExecuteReaderAsync(query, new { Keys = mediaKeys });
                while (await reader.ReadAsync())
                {
                    var deduKey = reader["dedup_key"]?.ToString();
                    var mediaKey = reader["media_key"]?.ToString();
                    if (!string.IsNullOrEmpty(deduKey) && !string.IsNullOrEmpty(mediaKey))
                        yield return (mediaKey, deduKey);
                }
            }
            finally
            {
                client.LockDB.Release();
            }
        }

        private static async Task<SQLiteConnection> GetConnection(GPMCClient client)
        {
            var path = await client.GetPathDB();
            if (!File.Exists(path)) throw new Exception("Файл с БД не найден");
            var conn = new SQLiteConnection($"Data Source={path};");
            await conn.OpenAsync();
            return conn;
        }
    }
}