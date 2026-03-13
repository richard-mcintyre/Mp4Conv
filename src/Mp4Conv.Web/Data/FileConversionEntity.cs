using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mp4Conv.Web.Data;

public class FileConversionEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public string FilePathAndName { get; set; } = String.Empty;

    public bool DeleteMkvFile { get; set; }

    public bool OverwriteMp4File { get; set; }

    public string VideoCodec { get; set; } = String.Empty;

    public string AudioCodec { get; set; } = String.Empty;

    public bool SetVideoQuality { get; set; }

    public int VideoQuality { get; set; }

    public FileConversionStatus Status { get; set; } = FileConversionStatus.NotStarted;

    public DateTime? StartedAt { get; set; }

    public DateTime? StatusChangedAt { get; set; }

    public string? StatusMessage { get; set; }
}


