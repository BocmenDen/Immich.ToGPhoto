using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Immich.ToGPhoto.App
{
    public class SyncDB : DbContext
    {
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
            return Convert.ToHexString(hash) + ".db";
        }
    }
}
