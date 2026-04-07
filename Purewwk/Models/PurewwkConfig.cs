using System.ComponentModel.DataAnnotations;

namespace Purewwk.Models;

public class PurewwkConfig
{
    [Required(AllowEmptyStrings = false)]
    public string MusicDirectory { get; set; } = string.Empty;

    public string? WorkingDirectory { get; set; }
}
