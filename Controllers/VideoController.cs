using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.IO;
using FFmpeg.AutoGen;
using System.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.IO.Pipelines;

namespace VideoChunking.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class VideoController : ControllerBase
    {
        private readonly string _tempFolder = Path.Combine(Directory.GetCurrentDirectory(), "temp");
        [HttpPost]
        public async Task<IActionResult> UploadVideo(IFormFile file)
        {
            var target = Path.Combine(_tempFolder, file.FileName);
            using (var stream = new FileStream(target, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            // Chunk the video file into multiple parts
            var chunkSize = 1024 * 1024; // 1MB chunks
            var buffer = new byte[chunkSize];
            using (var videoStream = new FileStream(target, FileMode.Open))
            {
                var bytesRead = 0;
                var chunkIndex = 0;
                while ((bytesRead = await videoStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    var chunkTarget = Path.Combine(_tempFolder, $"{file.FileName}.chunk{chunkIndex}");
                    using (var chunkStream = new FileStream(chunkTarget, FileMode.Create))
                    {
                        await chunkStream.WriteAsync(buffer, 0, bytesRead);
                    }
                    chunkIndex++;
                }
            }
            return Ok();
        }

        //[HttpGet("{fileName}")]
        //public async Task<FileStreamResult> StreamVideo(string fileName)
        //{
        //    var memoryStream = new MemoryStream();
        //    var chunkIndex = 0;
        //    byte[] buffer;

        //    while (true)
        //    {
        //        var chunkPath = Path.Combine(_tempFolder, $"{fileName}.chunk{chunkIndex}");

        //        if (!System.IO.File.Exists(chunkPath))
        //        {
        //            break;
        //        }

        //        using (var chunkStream = new FileStream(chunkPath, FileMode.Open))
        //        {
        //            buffer = new byte[chunkStream.Length];
        //            await chunkStream.ReadAsync(buffer, 0, (int)chunkStream.Length);
        //        }

        //        await memoryStream.WriteAsync(buffer, 0, buffer.Length);
        //        chunkIndex++;
        //    }

        //    memoryStream.Position = 0;
        //    return new FileStreamResult(memoryStream, "video/mp4");
        //}

        [HttpGet("{fileName}")]
        public async Task<IActionResult> StreamVideo(string fileName)
        {
            var range = Request.Headers["Range"].ToString();
            var rangeStart = 0L;
            var rangeEnd = 0L;
            var totalSize = Directory.GetFiles(_tempFolder, $"{fileName}.*").Sum(file => new FileInfo(file).Length);

            if (!string.IsNullOrEmpty(range))
            {
                var rangeParts = range.Replace("bytes=", "").Split('-');
                rangeStart = Convert.ToInt64(rangeParts[0]);
                rangeEnd = rangeParts.Length > 1 && !string.IsNullOrEmpty(rangeParts[1]) ? Convert.ToInt64(rangeParts[1]) : totalSize - 1;
            }
            else
            {
                rangeEnd = totalSize - 1;
            }

            var chunkFiles = Directory.GetFiles(_tempFolder, $"{fileName}.*").OrderBy(file => file).ToList();
            var chunkFileRanges = new List<(long Start, long End, string File)>();
            var position = 0L;

            foreach (var chunkFile in chunkFiles)
            {
                var length = new FileInfo(chunkFile).Length;
                chunkFileRanges.Add((position, position + length - 1, chunkFile));
                position += length;
            }

            var memoryStream = new MemoryStream();

            foreach (var (start, end, file) in chunkFileRanges)
            {
                if (end < rangeStart || start > rangeEnd)
                {
                    continue;
                }

                var bytesToRead = (int)Math.Min(end - Math.Max(start, rangeStart) + 1, rangeEnd - Math.Max(start, rangeStart) + 1);
                var offset = Math.Max(rangeStart - start, 0);

                using (var fileStream = new FileStream(file, FileMode.Open))
                {
                    fileStream.Seek(offset, SeekOrigin.Begin);
                    var buffer = new byte[bytesToRead];
                    await fileStream.ReadAsync(buffer, 0, bytesToRead);
                    await memoryStream.WriteAsync(buffer, 0, bytesToRead);
                    Response.Headers.Append("Content-Range", $"bytes {rangeStart}-{rangeEnd}/{totalSize}");
                    Response.Headers.Append("Accept-Ranges", "bytes");
                    Response.StatusCode = 206;
                    await fileStream.CopyToAsync(Response.Body, bytesToRead);
                }
            }
            memoryStream.Position = 0;
            return new FileStreamResult(memoryStream, "video/mp4");
        }

    }
}
