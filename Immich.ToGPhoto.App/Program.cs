using GPMC;
using Immich.Client;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Immich.ToGPhoto.App
{
    // Вдохновление: https://github.com/xob0t/gpmc/tree/main
    internal class Program
    {
        private const string IMMICH_TO_GPHOTO_CONFIG_PATH = nameof(IMMICH_TO_GPHOTO_CONFIG_PATH);

        static async Task Main(string[] args)
        {
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.IncludeScopes = true;
                });
            });
            var loggerInit = loggerFactory.CreateLogger("Program");
            string configPath = GetConfigPath(args);
            if (!File.Exists(configPath))
            {
                loggerInit.LogCritical($"Файл конфигурации по пути {{path}} не найден, задайте путь через переменные окружения как {IMMICH_TO_GPHOTO_CONFIG_PATH} или передайте в качестве первого параметра", configPath);
                return;
            }

            AppConfig? config = null;

            try
            {
                string json = File.ReadAllText(configPath);
                config = JsonConvert.DeserializeObject<AppConfig>(json);
                if (config == null)
                {
                    loggerInit.LogCritical("Не удалось десериализовать конфигурацию.");
                    return;
                }
            }
            catch (Exception e)
            {
                loggerInit.LogCritical(e, "Не удалось десериализовать конфигурацию.");
                return;
            }

            var users = await LoadUsers(config, loggerInit);

            while (true)
            {
                foreach (var user in users)
                {
                    try
                    {
                        var loggerScope = loggerFactory.CreateLogger(user.Name);
                        await user.SyncNewPhotos(loggerScope);
                    }
                    catch (Exception e)
                    {
                        using var scopeLogger = loggerInit.BeginScope(user.Name);
                        loggerInit.LogWarning(e, "При выполнении организации объектов произошла ошибка");
                    }
                }
                await Task.Delay(config.Timer);
            }
        }

        private static async Task<List<SyncUser>> LoadUsers(AppConfig appConfig, ILogger logger)
        {
            List<SyncUser> result = [];

            foreach (var userConfig in appConfig.SyncUserModels)
            {
                try
                {
                    ImmichClient immichClient = ImmichClientBuilder.Build(appConfig.HostImmich, userConfig.ImmichKey);
                    GPMCClient gpmcClient = GPMCClientBuilder.Build(appConfig.HostGPMC, userConfig.GPhotoKey);

                    SyncUser syncUser = new(immichClient, gpmcClient, await immichClient.GetUserName(), appConfig);
                    _ = await gpmcClient.UpdateCacheAsync();
                    var pathDB = await gpmcClient.GetDBPathAsync();
                    logger.LogInformation("Успешно добавлен пользователь {name} бд gpmc: {path}", syncUser.Name, pathDB.Path);
                    result.Add(syncUser);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "При конфигурирование пользователя с данными {userConfig} произошла ошибка", userConfig);
                }
            }

            return result;
        }

        private static string GetConfigPath(string[] args)
        {
            if (args.Length > 0 && !string.IsNullOrEmpty(args[0]))
                return args[0];

            var envPath = Environment.GetEnvironmentVariable(IMMICH_TO_GPHOTO_CONFIG_PATH);
            if (!string.IsNullOrEmpty(envPath))
                return envPath;

            return "config.json";
        }
    }
}
