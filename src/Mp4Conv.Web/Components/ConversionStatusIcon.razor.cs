using Microsoft.AspNetCore.Components;
using Mp4Conv.Web.Data;

namespace Mp4Conv.Web.Components;

public partial class ConversionStatusIcon : ComponentBase
{
    [Parameter]
    public FileConversionStatus Status { get; set; } = FileConversionStatus.NotStarted;

    [Parameter]
    public int Size { get; set; } = 20;
}
