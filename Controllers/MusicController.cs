using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Linq;
using repos.Models;
using repos.Services;

namespace repos.Controllers;

[ApiController]
[Route("rest")]
public class MusicController : ControllerBase
{
    private readonly LuceneService _luceneService;

    public MusicController(LuceneService luceneService)
    {
        _luceneService = luceneService;
    }

    [HttpGet("getMusicDirectory.view")]
    public IActionResult GetMusicDirectory(string id)
    {
        var children = _luceneService.GetChildren(id);
        var childList = children.Select(c => new ChildResponse
        {
            Id = c["id"],
            Parent = c["parent"],
            IsDir = c["isDir"] == "true",
            Title = c["title"],
            Path = c["path"]
        }).ToList();
        var dirResp = new DirectoryResponse
        {
            Id = id,
            Parent = id == "/" ? "" : string.Join("/", id.Split('/').SkipLast(1)),
            Name = id == "/" ? "music" : id.Split('/').Last(),
            Child = childList
        };
        var resp = new SubsonicResponse
        {
            Directory = dirResp
        };
        return Ok(new Dictionary<string, object> { ["subsonic-response"] = resp });
    }
}
