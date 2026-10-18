using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;

namespace DropKitWindows
{
    public sealed class PinterestAsset
    {
        public string PinId, OriginUrl, MediaUrl, Title, Kind, PreviewUrl, FilePath, ReplacedCoverPath;
        public int Width, Height;
    }
    public static class PinterestMedia
    {
        public static string DownloadFolder { get { return Path.Combine(ClipTransfer.ExportFolder, "Pinterest"); } }
        const long MaxDownload = 512L * 1024 * 1024;
        static readonly Regex PinPath = new Regex(@"^/pin/(?:[\w-]+--)?(\d+)/?$", RegexOptions.IgnoreCase);
        static readonly Regex Links = new Regex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase);
        static bool PinHost(string host)
        {
            return Regex.IsMatch(host, @"^(?:[a-z0-9-]+\.)?pinterest\.(?:com|co\.uk|com\.au|co\.kr|jp|fr|de|ca|it|es|pt|kr|tw|in|com\.mx)$", RegexOptions.IgnoreCase);
        }
        static bool CdnHost(string host) { return host.Equals("pinimg.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".pinimg.com", StringComparison.OrdinalIgnoreCase); }
        public static bool TrySource(string text, out Uri source)
        {
            source = null;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 2 * 1024 * 1024) return false;
            text = WebUtility.HtmlDecode(text).Replace("\\/", "/");
            foreach (Match match in Links.Matches(text))
            {
                Uri uri;
                if (!Uri.TryCreate(match.Value.TrimEnd(')', ']', ',', '.', ';'), UriKind.Absolute, out uri) || !string.IsNullOrEmpty(uri.UserInfo)) continue;
                if (PinHost(uri.Host) && PinPath.IsMatch(uri.AbsolutePath))
                { source = new Uri("https://www.pinterest.com/pin/" + PinPath.Match(uri.AbsolutePath).Groups[1].Value + "/"); return true; }
                if (uri.Host.Equals("pin.it", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Length > 1)
                { source = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri; return true; }
            }
            foreach (Match match in Links.Matches(text))
            {
                Uri uri;
                if (Uri.TryCreate(match.Value, UriKind.Absolute, out uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && CdnHost(uri.Host) &&
                    new[] { ".mp4", ".gif", ".jpg", ".jpeg", ".png", ".webp" }.Contains(Path.GetExtension(uri.AbsolutePath).ToLowerInvariant()))
                { source = uri; return true; }
            }
            return false;
        }
        public static bool TryDropSource(IDataObject data, out Uri source)
        { string hint; return TryDropSource(data, out source, out hint); }
        public static bool TryDropSource(IDataObject data, out Uri source, out string previewHint)
        {
            source = null;
            previewHint = null;
            if (data == null || ClipboardService.Sensitive(data)) return false;
            Uri fallback = null;
            foreach (string format in new[] { DataFormats.Html, DataFormats.UnicodeText, DataFormats.Text, "text/uri-list", "UniformResourceLocatorW", "UniformResourceLocator", "text/x-moz-url" })
            {
                if (!data.GetDataPresent(format, false)) continue;
                object value = data.GetData(format, false);
                string text = value as string;
                if (text == null)
                {
                    byte[] bytes = value as byte[];
                    var input = value as Stream;
                    if (bytes == null && input != null)
                    {
                        long old = input.CanSeek ? input.Position : 0;
                        try
                        {
                            if (input.CanSeek) input.Position = 0;
                            using (var output = new MemoryStream())
                            {
                                byte[] buffer = new byte[4096]; int read;
                                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                                { if (output.Length + read > 2 * 1024 * 1024) break; output.Write(buffer, 0, read); }
                                bytes = output.ToArray();
                            }
                        }
                        finally { if (input.CanSeek) input.Position = old; }
                    }
                    if (bytes != null && bytes.Length <= 2 * 1024 * 1024) text = (format.EndsWith("W") ? Encoding.Unicode : Encoding.UTF8).GetString(bytes).TrimEnd('\0');
                }
                if (string.IsNullOrEmpty(text) || text.Length > 2 * 1024 * 1024) continue;
                foreach (Match link in Links.Matches(WebUtility.HtmlDecode(text)))
                    if (MediaUrl(link.Value, new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" })) { previewHint = link.Value; break; }
                Uri candidate;
                if (!TrySource(text, out candidate)) continue;
                if (!CdnHost(candidate.Host)) { source = candidate; return true; }
                fallback = fallback ?? candidate;
            }
            source = fallback; return source != null;
        }
        static object Get(object value, string key)
        { var map = value as IDictionary<string, object>; object found; return map != null && map.TryGetValue(key, out found) ? found : null; }
        static string Str(object value, string key) { return Convert.ToString(Get(value, key)); }
        static int Number(object value, string key) { int number; return int.TryParse(Str(value, key), out number) ? number : 0; }
        static IEnumerable<object> Walk(object value, int depth = 0)
        {
            if (value == null || depth > 80) yield break;
            yield return value;
            var map = value as IDictionary<string, object>;
            if (map != null) { foreach (var child in map.Values) foreach (var item in Walk(child, depth + 1)) yield return item; }
            else if (!(value is string) && value is IEnumerable) foreach (object child in (IEnumerable)value) foreach (var item in Walk(child, depth + 1)) yield return item;
        }
        static bool MediaUrl(string value, string[] extensions)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && CdnHost(uri.Host) && extensions.Contains(Path.GetExtension(uri.AbsolutePath).ToLowerInvariant());
        }
        public static PinterestAsset ParsePin(string json, string id)
        { return ParsePin(json, id, null); }
        public static PinterestAsset ParsePin(string json, string id, string previewHint)
        {
            object tree = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 128 }.DeserializeObject(json);
            object pin = Walk(tree).FirstOrDefault(x => (Str(x, "id") == id || Str(x, "entityId") == id) && (Get(x, "images") != null || Get(x, "videos") != null));
            if (pin == null) throw new InvalidDataException("这个 Pinterest 链接没有可读取的公开素材。");
            string title = Str(pin, "title"); if (string.IsNullOrWhiteSpace(title)) title = Str(pin, "grid_title");
            var asset = new PinterestAsset { PinId = id, OriginUrl = "https://www.pinterest.com/pin/" + id + "/", Title = title };
            // Carousel videos belong to individual slots, not the top-level
            // videos field. Select the visible slot, never a neighbouring video.
            object carousel = Get(pin, "carousel_data") ?? Get(pin, "carouselData");
            object[] slots = (Get(carousel, "carousel_slots") ?? Get(carousel, "carouselSlots")) as object[];
            object selected = pin;
            if (slots != null && slots.Length > 0)
            {
                string signature = ImageSignature(previewHint);
                if (string.IsNullOrEmpty(signature)) signature = Str(pin, "image_signature");
                if (string.IsNullOrEmpty(signature)) signature = ImageSignature(Str(Get(Get(pin, "images"), "orig"), "url"));
                object matched = !string.IsNullOrEmpty(signature) ? slots.FirstOrDefault(slot => Walk(slot).Any(x => ImageSignature(Str(x, "url")) == signature || ImageSignature(Str(x, "thumbnail")) == signature)) : null;
                int index = Number(carousel, "index");
                selected = matched ?? slots[Math.Max(0, Math.Min(slots.Length - 1, index))];
            }
            var videoNodes = Walk(Get(selected, "videos")).Concat(Walk(Get(selected, "story_pin_data"))).Concat(Walk(Get(selected, "storyPinData"))).ToArray();
            var videos = videoNodes.Where(x => MediaUrl(Str(x, "url"), new[] { ".mp4" })).OrderByDescending(x => (long)Number(x, "width") * Number(x, "height")).ToArray();
            if (videos.Length > 0)
            {
                var video = videos[0]; asset.MediaUrl = Str(video, "url"); asset.Kind = "视频";
                asset.Width = Number(video, "width"); asset.Height = Number(video, "height"); asset.PreviewUrl = Str(video, "thumbnail");
                string cover = Str(Get(Get(pin, "images"), "orig"), "url");
                if (MediaUrl(cover, new[] { ".jpg", ".jpeg", ".png", ".webp" }) && (selected == pin || Walk(Get(selected, "images")).Any(x => ImageSignature(Str(x, "url")) == ImageSignature(cover)))) asset.ReplacedCoverPath = cover;
                return asset;
            }
            if (videoNodes.Any(x => MediaUrl(Str(x, "url"), new[] { ".m3u8" })))
                throw new NotSupportedException("这个视频只提供流式播放地址，目前无法直接保存；原链接已保留。");
            object images = Get(selected, "images");
            // Keep the original full-size image for the default visible slot.
            if (selected != pin && Walk(images).Any(x => ImageSignature(Str(x, "url")) == ImageSignature(Str(Get(Get(pin, "images"), "orig"), "url")))) images = Get(pin, "images");
            object image = Get(images, "orig") ?? Get(images, "originals");
            if (!MediaUrl(Str(image, "url"), new[] { ".gif", ".jpg", ".jpeg", ".png", ".webp" }))
                image = Walk(images).Where(x => MediaUrl(Str(x, "url"), new[] { ".gif", ".jpg", ".jpeg", ".png", ".webp" })).OrderByDescending(x => (long)Number(x, "width") * Number(x, "height")).FirstOrDefault();
            if (image == null) throw new InvalidDataException("这个 Pinterest 链接没有可保存的视频或原图。");
            asset.MediaUrl = Str(image, "url"); asset.Kind = Path.GetExtension(new Uri(asset.MediaUrl).AbsolutePath).Equals(".gif", StringComparison.OrdinalIgnoreCase) ? "动图" : "图片";
            asset.Width = Number(image, "width"); asset.Height = Number(image, "height"); return asset;
        }
        static string ImageSignature(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !CdnHost(uri.Host)) return "";
            string name = Path.GetFileName(uri.AbsolutePath);
            var hash = Regex.Match(name, @"^[a-f0-9]{32}", RegexOptions.IgnoreCase);
            return hash.Success ? hash.Value.ToLowerInvariant() : "";
        }
        static HttpWebResponse Open(Uri uri, CancellationToken cancellation, bool media, int redirects = 0)
        {
            if (redirects > 5 || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !(media ? CdnHost(uri.Host) : PinHost(uri.Host) || uri.Host.Equals("pin.it", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("素材地址不是受支持的 Pinterest 链接。");
            cancellation.ThrowIfCancellationRequested();
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(uri); request.AllowAutoRedirect = false;
            // .NET Framework does not automatically honor the standard proxy
            // environment used by modern Windows browsers/HTTP clients.
            string proxyValue = Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? Environment.GetEnvironmentVariable("https_proxy") ?? Environment.GetEnvironmentVariable("HTTP_PROXY");
            Uri proxyUri;
            if (Uri.TryCreate(proxyValue, UriKind.Absolute, out proxyUri) && (proxyUri.Scheme == "http" || proxyUri.Scheme == "https"))
            {
                var address = new UriBuilder(proxyUri) { UserName = "", Password = "" }.Uri;
                var proxy = new WebProxy(address);
                if (!string.IsNullOrEmpty(proxyUri.UserInfo))
                {
                    string[] credentials = proxyUri.UserInfo.Split(new[] { ':' }, 2);
                    proxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(credentials[0]), credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : "");
                }
                request.Proxy = proxy;
            }
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36";
            request.Timeout = 30000; request.ReadWriteTimeout = 30000; request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (!media) request.Headers["X-Pinterest-PWS-Handler"] = "www/[username].js";
            HttpWebResponse response;
            using (cancellation.Register(request.Abort)) response = (HttpWebResponse)request.GetResponse();
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            {
                Uri next = new Uri(uri, response.Headers["Location"]); response.Dispose(); return Open(next, cancellation, media, redirects + 1);
            }
            return response;
        }
        static string ReadText(Uri uri, CancellationToken cancellation, out Uri final)
        {
            using (var response = Open(uri, cancellation, false))
            using (var stream = response.GetResponseStream())
            using (var output = new MemoryStream())
            {
                final = response.ResponseUri; var bytes = new byte[8192]; int read;
                while ((read = stream.Read(bytes, 0, bytes.Length)) > 0)
                { cancellation.ThrowIfCancellationRequested(); if (output.Length + read > 16 * 1024 * 1024) throw new InvalidDataException("素材页面超过读取限制。"); output.Write(bytes, 0, read); }
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
        public static PinterestAsset Resolve(Uri source, CancellationToken cancellation)
        { return Resolve(source, cancellation, null); }
        public static PinterestAsset Resolve(Uri source, CancellationToken cancellation, string previewHint)
        {
            if (CdnHost(source.Host)) return new PinterestAsset { OriginUrl = source.AbsoluteUri, MediaUrl = source.AbsoluteUri, PinId = "media", Kind = Path.GetExtension(source.AbsolutePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? "视频" : Path.GetExtension(source.AbsolutePath).Equals(".gif", StringComparison.OrdinalIgnoreCase) ? "动图" : "图片" };
            Uri final; string html = null;
            if (source.Host.Equals("pin.it", StringComparison.OrdinalIgnoreCase)) { html = ReadText(source, cancellation, out final); source = final; }
            var match = PinPath.Match(source.AbsolutePath); if (!PinHost(source.Host) || !match.Success) throw new InvalidDataException("请拖入 Pinterest 单条素材链接。");
            string id = match.Groups[1].Value;
            string data = new JavaScriptSerializer().Serialize(new { options = new { field_set_key = "unauth_react_main_pin", id = id } });
            try { return ParsePin(ReadText(new Uri("https://www.pinterest.com/resource/PinResource/get/?data=" + Uri.EscapeDataString(data)), cancellation, out final), id, previewHint); }
            catch (OperationCanceledException) { throw; }
            catch (NotSupportedException) { throw; }
            catch (Exception)
            {
                if (html == null) html = ReadText(source, cancellation, out final);
                foreach (Match script in Regex.Matches(html, @"<script\b[^>]*type=['"" ]application/json['""][^>]*>([\s\S]*?)</script>", RegexOptions.IgnoreCase))
                    try { return ParsePin(script.Groups[1].Value, id, previewHint); } catch (InvalidDataException) { } catch (ArgumentException) { }
                throw new InvalidDataException("未能读取公开素材。可能是链接已失效或需要登录；原链接已保留。");
            }
        }
        static string Digest(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        static string FileDigest(string path) { using (var sha = SHA256.Create()) using (var file = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); }
        public static bool IsMediaFile(string path)
        {
            if (!File.Exists(path) || new FileInfo(path).Length < 12) return false;
            var header = new byte[16]; using (var stream = File.OpenRead(path)) stream.Read(header, 0, header.Length);
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".mp4": return Encoding.ASCII.GetString(header, 4, 4) == "ftyp";
                case ".gif": return Encoding.ASCII.GetString(header, 0, 6) == "GIF87a" || Encoding.ASCII.GetString(header, 0, 6) == "GIF89a";
                case ".jpg": case ".jpeg": return header[0] == 255 && header[1] == 216 && header[2] == 255;
                case ".png": return header[0] == 137 && Encoding.ASCII.GetString(header, 1, 3) == "PNG";
                case ".webp": return Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WEBP";
                default: return false;
            }
        }
        public static PinterestAsset Download(Uri source, string folder, CancellationToken cancellation, Action<string> progress)
        { return Download(source, folder, cancellation, progress, null); }
        public static PinterestAsset Download(Uri source, string folder, CancellationToken cancellation, Action<string> progress, string previewHint)
        {
            if (progress != null) progress("正在解析 Pinterest 素材…");
            var asset = Resolve(source, cancellation, previewHint); Directory.CreateDirectory(folder);
            string extension = Path.GetExtension(new Uri(asset.MediaUrl).AbsolutePath).ToLowerInvariant();
            string path = Path.Combine(folder, "Pinterest_" + asset.PinId + "_" + Digest(Encoding.UTF8.GetBytes(asset.MediaUrl)).Substring(0, 12) + extension);
            string manifest = path + ".source.json";
            if (!string.IsNullOrEmpty(asset.ReplacedCoverPath)) asset.ReplacedCoverPath = Path.Combine(folder, "Pinterest_" + asset.PinId + "_" + Digest(Encoding.UTF8.GetBytes(asset.ReplacedCoverPath)).Substring(0, 12) + Path.GetExtension(new Uri(asset.ReplacedCoverPath).AbsolutePath));
            if (IsMediaFile(path) && File.Exists(manifest))
            {
                try { var stored = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(manifest)); if (Str(stored, "sha256") == FileDigest(path)) { asset.FilePath = path; return asset; } } catch { }
            }
            // Do not replace an existing file when its integrity cannot be confirmed.
            if (File.Exists(path)) { path = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + extension); manifest = path + ".source.json"; }
            SaveFile(new Uri(asset.MediaUrl), path, cancellation, progress, MaxDownload);
            asset.FilePath = path;
            File.WriteAllText(manifest, new JavaScriptSerializer().Serialize(new { origin_url = asset.OriginUrl, media_url = asset.MediaUrl, title = asset.Title, kind = asset.Kind, sha256 = FileDigest(path) }), new UTF8Encoding(false));
            if (asset.Kind == "视频" && MediaUrl(asset.PreviewUrl, new[] { ".jpg", ".jpeg", ".png" }))
                try { SaveFile(new Uri(asset.PreviewUrl), path + ".preview.jpg", cancellation, null, 8 * 1024 * 1024); } catch (OperationCanceledException) { throw; } catch { }
            return asset;
        }
        static void SaveFile(Uri uri, string path, CancellationToken cancellation, Action<string> progress, long maximum)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using (var response = Open(uri, cancellation, true))
                using (var stream = response.GetResponseStream())
                using (var file = File.Create(temporary))
                {
                    if (response.ContentLength > maximum) throw new InvalidDataException("素材超过 512 MB 下载限制。");
                    var buffer = new byte[65536]; int read, lastPercent = -1; long size = 0;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellation.ThrowIfCancellationRequested(); size += read; if (size > maximum) throw new InvalidDataException("素材超过下载大小限制。");
                        file.Write(buffer, 0, read);
                        int percent = response.ContentLength > 0 ? (int)(size * 100 / response.ContentLength) : 0;
                        if (progress != null && percent != lastPercent) { lastPercent = percent; progress("正在保存 Pinterest 素材… " + (response.ContentLength > 0 ? percent + "%" : (size / 1024) + " KB")); }
                    }
                    if (response.ContentLength >= 0 && size != response.ContentLength) throw new IOException("下载未完成，请重新拖入链接。");
                }
                // Check using the real extension, before committing the file.
                string check = Path.Combine(Path.GetDirectoryName(path), ".validate-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
                File.Move(temporary, check); temporary = check;
                if (!IsMediaFile(check)) throw new InvalidDataException("服务器没有返回有效的素材文件；原链接已保留。");
                cancellation.ThrowIfCancellationRequested(); File.Move(check, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
