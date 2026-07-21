using Dapper;
using System.Data.SQLite;

namespace GPMC
{
    public static class ClientDBExtensions
    {
        public static async IAsyncEnumerable<string> GetMediaKeys(this GPMCClient client)
        {
            await client.LockDB.WaitAsync();
            try
            {
                using var conn = await GetConnection(client);
                var cmd = new SQLiteCommand("SELECT media_key FROM remote_media", conn);
                using var reader = cmd.ExecuteReader();
                while (await reader.ReadAsync())
                {
                    var key = reader["media_key"]?.ToString();
                    if (!string.IsNullOrEmpty(key))
                        yield return key;
                }
            }
            finally
            {
                client.LockDB.Release();
            }
        }

        public static async IAsyncEnumerable<string> Intersect(this GPMCClient client, IEnumerable<string> mediaKey)
        {
            await client.LockDB.WaitAsync();
            try
            {
                using var conn = await GetConnection(client);
                var query = "SELECT media_key FROM remote_media WHERE media_key IN @Keys";
                using var reader = await conn.ExecuteReaderAsync(query, new { Keys = mediaKey });
                while (await reader.ReadAsync())
                {
                    var key = reader["media_key"]?.ToString();
                    if (!string.IsNullOrEmpty(key))
                        yield return key;
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