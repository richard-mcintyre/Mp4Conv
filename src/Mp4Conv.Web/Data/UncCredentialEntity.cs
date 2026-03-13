namespace Mp4Conv.Web.Data;

public class UncCredentialEntity
{
    public int Id { get; set; }

    public string UncPath { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string EncryptedPassword { get; set; } = string.Empty;
}
