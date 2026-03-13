using Microsoft.EntityFrameworkCore;

namespace Mp4Conv.Web.Data;

public class Mp4ConvDbContext : DbContext
{
    public Mp4ConvDbContext(DbContextOptions<Mp4ConvDbContext> options) 
        : base(options) 
    { 
    }

    public DbSet<FileConversionEntity> ConversionQueue => Set<FileConversionEntity>();

    public DbSet<ConfigSettingsEntity> ConfigSettings => Set<ConfigSettingsEntity>();

    public DbSet<DropSourceFolderEntity> DropSourceFolders => Set<DropSourceFolderEntity>();

    public DbSet<UncCredentialEntity> UncCredentials => Set<UncCredentialEntity>();
}
