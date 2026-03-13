using System.Runtime.InteropServices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Mp4Conv.Web.Data;

namespace Mp4Conv.Web.Services;

public class UncConnectionService
{
    private const string Purpose = "UncCredentials";

    private readonly IDbContextFactory<Mp4ConvDbContext> _dbContextFactory;
    private readonly IDataProtector _protector;
    private readonly ILogger<UncConnectionService> _logger;

    public UncConnectionService(
        IDbContextFactory<Mp4ConvDbContext> dbContextFactory,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<UncConnectionService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _protector = dataProtectionProvider.CreateProtector(Purpose);
        _logger = logger;
    }

    public string ProtectPassword(string plaintext) => _protector.Protect(plaintext);

    /// <summary>
    /// Re-establishes all stored UNC connections. Called at startup and from the settings UI.
    /// Returns one error string per path that failed; an empty list means full success.
    /// </summary>
    public async Task<IReadOnlyList<string>> ConnectAllAsync()
    {
        List<string> errors = [];

        await using Mp4ConvDbContext context = await _dbContextFactory.CreateDbContextAsync();
        List<UncCredentialEntity> credentials = await context.UncCredentials.ToListAsync();

        foreach (UncCredentialEntity cred in credentials)
        {
            try
            {
                string password = _protector.Unprotect(cred.EncryptedPassword);
                Connect(cred.UncPath, cred.Username, password);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to UNC path {UncPath}.", cred.UncPath);
                errors.Add($"{cred.UncPath}: {ex.Message}");
            }
        }

        return errors;
    }

    /// <summary>
    /// Authenticates the current process against a UNC share without mapping a drive letter.
    /// Throws <see cref="InvalidOperationException"/> if the connection fails.
    /// </summary>
    public void Connect(string uncPath, string username, string password)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        // Cancel any stale connection first (ignore errors)
        WNetCancelConnection2(uncPath, 0, true);

        NETRESOURCE netResource = new()
        {
            dwScope = 2,        // RESOURCE_GLOBALNET
            dwType = 1,         // RESOURCETYPE_DISK
            dwDisplayType = 3,  // RESOURCEDISPLAYTYPE_SHARE
            dwUsage = 1,        // RESOURCEUSAGE_CONNECTABLE
            lpLocalName = null, // no drive letter
            lpRemoteName = uncPath,
            lpComment = null,
            lpProvider = null
        };

        int result = WNetAddConnection2(ref netResource, password, username, 0);
        if (result != 0)
            throw new InvalidOperationException(
                $"WNetAddConnection2 failed with error {result} for path '{uncPath}'.");

        _logger.LogInformation("Connected to UNC path {UncPath} as {Username}.", uncPath, username);
    }

    public void Disconnect(string uncPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        WNetCancelConnection2(uncPath, 0, true);
        _logger.LogInformation("Disconnected from UNC path {UncPath}.", uncPath);
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NETRESOURCE netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }
}
