using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Immich.ToGPhoto.App
{
    public class SyncDB : DbContext
    {
        public const string SYNC_DB_BASE_PATH = nameof(SYNC_DB_BASE_PATH);

        public DbSet<SyncItemModel> SyncItems { get; init; }

        public SyncDB(string key) : base(new DbContextOptionsBuilder<SyncDB>().UseSqlite($"Data Source={GetDbFileName(key)}").Options)
        {
            SyncItems = Set<SyncItemModel>();
            Database.EnsureCreated();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SyncItemModel>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Id)
                      .ValueGeneratedOnAdd();

                entity.HasIndex(e => e.ImmichKey)
                      .IsUnique();

                entity.HasIndex(e => e.GoogleKey)
                      .IsUnique();
            });
        }

        private static string GetDbFileName(string key)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var path = Convert.ToHexString(hash) + ".db";
            var pathSave = Environment.GetEnvironmentVariable(SYNC_DB_BASE_PATH);
            if (pathSave == null) return path;
            return Path.Combine(pathSave, path);
        }
    }
}