using GPMC;
using Immich.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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

        private SyncUser() { }

        public SyncUser(ImmichClient immichClient, GPMCClient gpmcClient, string name)
        {
            _gpmcClient = gpmcClient;
            _immichClient = immichClient;
            Key = $"{_gpmcClient.Key}_{immichClient.Key}";
            _syncDB = new SyncDB(Key);
            Name = name;
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
            await foreach (var chank in _gpmcClient.GetMediaKeys().Chunk(CHANK_SIZE))
            {
                var findElems = await _syncDB.SyncItems.Where(x => chank.Contains(x.GoogleKey)).ToListAsync();
                var deleteElems = chank.Except(findElems.Select(x => x.GoogleKey));
                toDeleate.AddRange(deleteElems);
            }

            logger.LogInformation("В Google Photo обнаружены [{count}] элементов которые отсутствуют в Immich они будут удалены", toDeleate.Count);

            foreach (var chank in toDeleate.Chunk(CHANK_SIZE))
                await _gpmcClient.DeleteFilesAsync(new() { Files = chank });

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

                var existingInGoogle = await _gpmcClient.Intersect(googleKeys).ToListAsync();

                if (existingInGoogle.Count == 0) continue;

                logger.LogInformation("Обнаружено {count} элементов в Google Photo которые в Immich лежат в корзине", existingInGoogle.Count);

                _ = await _gpmcClient.DeleteFilesAsync(new() { Files = existingInGoogle });

                await _gpmcClient.UpdateCacheAsync();
                var remainingInGoogle = await _gpmcClient.Intersect(googleKeys).ToListAsync();

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
            var allAssetsImmich = _immichClient.SearchAllAssetsAsync(new MetadataSearchDto() { WithDeleted = false, WithStacked = true }).Select(x => x.Id);

            await foreach (var itemImmichChank in allAssetsImmich.Chunk(CHANK_SIZE))
            {
                // Те элементы которые вроде как загружены
                var keysDBUploaded = await _syncDB.SyncItems.Where(x => itemImmichChank.Contains(x.ImmichKey)).ToListAsync();
                // Те элементы которые в действительности загружены в Google Photos. (Синхронизация с Google Photos)
                var toUpload = await _gpmcClient.Intersect(keysDBUploaded.Select(x => x.GoogleKey)).ToHashSetAsync();
                // Те элементы которые были случайно удалены в гугл фото
                var toResetUpload = keysDBUploaded.Where(x => !toUpload.Contains(x.GoogleKey));
                // Очистили бд от тех элементов которые будем повторно загружать
                logger.LogInformation("Обнаружены удалённые элементы в Google Photo которые присутствуют в Immich");
                _syncDB.SyncItems.RemoveRange(toResetUpload);
                await _syncDB.SaveChangesAsync();
                // Обнаружение фото требующих загрузки в Google Photos.
                var toNewUpload = itemImmichChank.Except(keysDBUploaded.Select(x => x.ImmichKey));
                // Элементы которые в конечном итоге загружаем
                var toFillUpload = toNewUpload.Concat(toResetUpload.Select(x => x.ImmichKey));

                var (pathFilesUpload, mapFilesKey) = await GetFilesToUpload(toFillUpload, logger);

                logger.LogInformation("В Google Photo успешно загружено {count} элементов", mapFilesKey.Count);

                var resultUpload = (await _gpmcClient.UploadFilesAsync(new UploadRequest() { Path = pathFilesUpload })).Files;

                var addDBItems = resultUpload.Select(x => new SyncItemModel() { GoogleKey = x.Value, ImmichKey = mapFilesKey[new(x.Key)] });

                await _syncDB.SyncItems.AddRangeAsync(addDBItems);
                await _syncDB.SaveChangesAsync();

                Directory.Delete(pathFilesUpload, true);
            }
        }

        private async Task<(string, Dictionary<Uri, Guid>)> GetFilesToUpload(IEnumerable<Guid> files, ILogger logger)
        {
            var uploadFolder = Path.Combine(
                    Path.GetTempPath(),
                    "immich_upload_" + Guid.NewGuid()
                );

            Directory.CreateDirectory(uploadFolder);

            var mapping = new Dictionary<Uri, Guid>();

            foreach (var id in files)
            {
                var asset = await _immichClient.GetAssetInfoAsync(id);

                //   asset.IsEdited  Todo На будущее. Если фото было отредактировано, то нужно скачать оригинал и применить метаданные из ExifInfo. (Дата, GPS координаты). А со стороны GPhoto удалить и заново загрузить Ибо API редактирования нет

                var fileResponse = await _immichClient.DownloadAssetAsync(id);


                var tempSubFolder = Path.GetFullPath(Path.Combine(
                    uploadFolder,
                    Guid.NewGuid().ToString()
                ));

                Directory.CreateDirectory(tempSubFolder);

                var filePath = Path.Combine(tempSubFolder, asset.OriginalFileName);

                using var file = File.OpenWrite(filePath);

                await fileResponse.Stream.CopyToAsync(file);

                file.Close();

                logger.LogInformation("Скачен новый элемент для загрузки {path}", filePath);

                mapping[new(filePath)] = id;
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