using GPMC;
using Immich.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Immich.ToGPhoto.App
{
    public class SyncUser : IDisposable
    {
        private readonly static DateTime TrashedAfter = DateTime.Parse("1970-01-01T00:00:00.000Z");
        private const int CHANK_SIZE = 1000;

        public string Name { get; private set; } = null!;
        public readonly string Key = null!;
        private readonly GPMCClient _gpmcClient = null!;
        private readonly ImmichClient _immichClient = null!;
        private readonly SyncDB _syncDB = null!;
        private readonly SyncUserConfig _config = null!;

        private SyncUser() { }

        public SyncUser(ImmichClient immichClient, GPMCClient gpmcClient, string name, SyncUserConfig? syncUserConfig = null)
        {
            _gpmcClient = gpmcClient;
            _immichClient = immichClient;
            Key = $"{_gpmcClient.Key}_{immichClient.Key}";
            _syncDB = new SyncDB(Key);
            Name = name;
            _config = syncUserConfig ?? new();
        }

        public async Task SyncNewPhotos(ILogger logger)
        {
            await _gpmcClient.UpdateCacheAsync();
            await LoadPhotos(logger);
            await DeletePhotos(logger);
            await DeleteAnotherPhotos(logger);
        }

        /// <summary>
        /// Удаление фото которые не были загружены в Immich, но есть в Google Photos
        /// </summary>
        private async Task DeleteAnotherPhotos(ILogger logger)
        {
            List<string> toDeleate = [];
            await foreach (var chank in _gpmcClient.GetKeys().Chunk(CHANK_SIZE))
            {
                var findElems = await _syncDB.SyncItems.Where(x => chank.Contains(x.GoogleKey)).ToListAsync();
                var deleteElems = chank.Except(findElems.Select(x => x.GoogleKey));
                toDeleate.AddRange(deleteElems);
            }

            logger.LogInformation("В Google Photo обнаружены [{count}] элементов которые отсутствуют в Immich они будут удалены", toDeleate.Count);

            await _gpmcClient.DeleteFilesAsync(new() { DedupKeys = toDeleate });

            logger.LogInformation("В Google Photo удалены [{count}] элементов которые отсутствуют в Immich", toDeleate.Count);
        }
        /// <summary>
        /// Гарантия что фото в корзине будут удалены из GPhoto
        /// </summary>
        private async Task DeletePhotos(ILogger logger)
        {
            var allAssetsImmich = _immichClient.SearchAllAssetsAsync(new MetadataSearchDto() { WithDeleted = true, WithStacked = true, TrashedAfter = TrashedAfter }).Where(x => x.IsTrashed).Select(x => x.Id);
            await foreach (var itemImmichChank in allAssetsImmich.Chunk(CHANK_SIZE))
            {
                var syncItems = await _syncDB.SyncItems.Where(x => itemImmichChank.Contains(x.ImmichKey)).ToListAsync();
                var googleKeys = syncItems.Select(x => x.GoogleKey).ToList();

                var existingInGoogle = await _gpmcClient.IntersectKeys(googleKeys).ToListAsync();

                if (existingInGoogle.Count == 0) continue;

                logger.LogInformation("Обнаружено {count} элементов в Google Photo которые в Immich лежат в корзине", existingInGoogle.Count);

                _ = await _gpmcClient.DeleteFilesAsync(new() { DedupKeys = existingInGoogle });

                await _gpmcClient.UpdateCacheAsync();
                var remainingInGoogle = await _gpmcClient.IntersectKeys(googleKeys).ToListAsync();

                var removedFromGoogle = googleKeys.Except(remainingInGoogle).ToHashSet();
                logger.LogInformation("Удалены {count} элементов в Google Photo которые в Immich лежат в корзине", removedFromGoogle.Count);
                var syncItemsToRemove = syncItems.Where(x => removedFromGoogle.Contains(x.GoogleKey));

                _syncDB.SyncItems.RemoveRange(syncItemsToRemove);
                await _syncDB.SaveChangesAsync();
            }
        }
        /// <summary>
        /// Гарантия что все фото загружены
        /// </summary>
        private async Task LoadPhotos(ILogger logger)
        {
            var allAssetsImmich = _immichClient.SearchAllAssetsAsync(new MetadataSearchDto() { WithDeleted = false, WithStacked = true, Order = AssetOrder.Asc });

            await foreach (var itemImmichChank in allAssetsImmich.Take(_config.TakeUpload).Chunk(CHANK_SIZE))
            {
                // Те элементы которые вроде как загружены
                var keysDBUploaded = await _syncDB.SyncItems.Where(x => itemImmichChank.Select(x => x.Id).Contains(x.ImmichKey)).ToListAsync();
                // Те элементы которые в действительности загружены в Google Photos. (Синхронизация с Google Photos)
                var toUpload = await _gpmcClient.IntersectKeys(keysDBUploaded.Select(x => x.GoogleKey)).ToHashSetAsync();
                // Те элементы которые были случайно удалены в гугл фото
                var toResetUpload = keysDBUploaded.Where(x => !toUpload.Contains(x.GoogleKey));
                // Очистили бд от тех элементов которые будем повторно загружать
                if (toResetUpload.Any())
                {
                    logger.LogInformation("Обнаружены удалённые элементы в Google Photo которые присутствуют в Immich");
                    _syncDB.SyncItems.RemoveRange(toResetUpload);
                    await _syncDB.SaveChangesAsync();
                }
                // Обнаружение фото требующих загрузки в Google Photos.
                var toNewUpload = itemImmichChank.ExceptBy(keysDBUploaded.Select(x => x.ImmichKey), x => x.Id).OrderBy(x => x.ExifInfo?.DateTimeOriginal).Select(x => x.Id);
                // Элементы которые в конечном итоге загружаем
                var toFillUpload = toNewUpload.Concat(toResetUpload.Select(x => x.ImmichKey));

                foreach (var toFillUploadChank in toFillUpload.Chunk(_config.CountChankUpload))
                {
                    var (pathFilesUpload, mapFilesKey) = await GetFilesToUpload(toFillUploadChank, logger);

                    logger.LogInformation("Успешно скачано для загрузки {count} элементов", mapFilesKey.Count);

                    var resultUpload = await _gpmcClient.UploadFilesAsync(new UploadRequest() { Path = pathFilesUpload, Threads = _config.CountThreadsGPMC });

                    logger.LogInformation("В Google Photo успешно загружено {count} элементов", resultUpload.Count);

                    await CheckConstraintConfict(resultUpload);

                    var addDBItems = resultUpload.Select(x => new SyncItemModel() { GoogleKey = x.Value, ImmichKey = mapFilesKey[x.Key] });

                    await _syncDB.SyncItems.AddRangeAsync(addDBItems);
                    await _syncDB.SaveChangesAsync();

                    Directory.Delete(pathFilesUpload, true);
                }
            }
        }

        private async Task CheckConstraintConfict(IDictionary<Uri, string> resultUpload)
        {
            await using var conflictItems = GetConflictItems(resultUpload).GetAsyncEnumerator();
            if (await conflictItems.MoveNextAsync())
            {
                StringBuilder stringBuilder = new("Найдены дубликаты в Immich которые в Google Photo помечены одним ключом\n");
                do
                {
                    var (fileName, googleKey) = conflictItems.Current;
                    stringBuilder.AppendLine($"Имя файла {fileName} GoogeKey {googleKey}");
                } while (await conflictItems.MoveNextAsync());
                throw new Exception(stringBuilder.ToString());
            }
        }

        private async IAsyncEnumerable<(string fileName, string googleKey)> GetConflictItems(IDictionary<Uri, string> resultUpload)
        {
            foreach (var item in _syncDB.SyncItems.Where(x => resultUpload.Values.Contains(x.GoogleKey)))
            {
                var fileInfo = await _immichClient.GetAssetInfoAsync(item.ImmichKey);
                yield return (fileInfo.OriginalFileName, item.GoogleKey);
            }

            foreach (var item in resultUpload.GroupBy(x => x.Value).Where(x => x.Count() > 1))
            {
                foreach (var (confictFile, key) in item)
                {
                    yield return (key, confictFile.ToString());
                }
            }
        }

        private async Task<(string, IDictionary<Uri, Guid>)> GetFilesToUpload(IEnumerable<Guid> files, ILogger logger)
        {
            var uploadFolder = Path.Combine(
                    Path.GetTempPath(),
                    "immich_upload_" + Guid.NewGuid()
                );

            Directory.CreateDirectory(uploadFolder);

            var mapping = new ConcurrentDictionary<Uri, Guid>();

            async Task fileDownload(Guid id)
            {
                var asset = await _immichClient.GetAssetInfoAsync(id);

                //   asset.IsEdited  Todo На будущее. Если фото было отредактировано, то нужно скачать оригинал и применить метаданные из ExifInfo. (Дата, GPS координаты). А со стороны GPhoto удалить и заново загрузить Ибо API редактирования нет

                var fileResponse = await _immichClient.DownloadAssetAsync(id, edited: true);


                var tempSubFolder = Path.GetFullPath(Path.Combine(
                    uploadFolder,
                    Guid.NewGuid().ToString()
                ));

                Directory.CreateDirectory(tempSubFolder);

                var filePath = Path.Combine(tempSubFolder, asset.OriginalFileName);

                using var file = File.OpenWrite(filePath);

                await fileResponse.Stream.CopyToAsync(file);

                file.Close();

                try
                {
                    using var exiftool = new SharpExifTool.ExifTool();
                    var tags = new Dictionary<string, string>();

                    if (asset.ExifInfo?.DateTimeOriginal != null)
                    {
                        tags["DateTimeOriginal"] =
                            asset.ExifInfo.DateTimeOriginal
                                .Value
                                .ToString("yyyy:MM:dd HH:mm:ss");
                    }

                    if (asset.ExifInfo?.Latitude != null)
                    {
                        tags["GPSLatitude"] =
                            asset.ExifInfo.Latitude.Value.ToString(
                                CultureInfo.InvariantCulture);
                    }

                    if (asset.ExifInfo?.Longitude != null)
                    {
                        tags["GPSLongitude"] =
                            asset.ExifInfo.Longitude.Value.ToString(
                                CultureInfo.InvariantCulture);
                    }

                    await exiftool.WriteTagsAsync(filePath, tags);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Не удалось применить exiftool к фото {path}", filePath);
                }

                logger.LogInformation("Скачен новый элемент для загрузки {path}", filePath);

                mapping[new(filePath)] = id;
            }

            foreach (var chankDownload in files.Chunk(_config.CountThreadsImmich))
            {
                var tasksWait = chankDownload.Select(x => fileDownload(x));
                await Task.WhenAll(tasksWait);
            }

            return (uploadFolder, mapping);
        }

        public void Dispose()
        {
            _syncDB.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}