using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using IndustryPlanner.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace IndustryPlanner.EditorTools
{
    /// <summary>
    /// Menu: IndustryPlanner > Download Satisfactory icons.
    /// Looks up every item and building image on satisfactory.wiki.gg through the MediaWiki API, downloads a
    /// 128px version and saves it as Assets/Resources/Icons/Satisfactory/{Items|Buildings}/{id}.png, where the
    /// app picks it up instead of the placeholder. Existing files are kept, so your own art is never replaced.
    /// </summary>
    public static class WikiIconDownloader
    {
        const string Api = "https://satisfactory.wiki.gg/api.php";
        const string GameFile = "Assets/Resources/Games/satisfactory.json";
        const string IconRoot = "Assets/Resources/Icons/Satisfactory";
        const int BatchSize = 50; // MediaWiki's limit for titles per query.

        // Buildings whose in-app name differs from their wiki file name.
        static readonly Dictionary<string, string> WikiNames = new()
        {
            { "Desc_ConveyorAttachmentSplitter_C", "Conveyor Splitter" },
            { "Desc_ConveyorAttachmentMerger_C", "Conveyor Merger" },
        };

        [System.Serializable] class ApiResponse { public Query query; }
        [System.Serializable] class Query { public Page[] pages; }
        [System.Serializable] class Page { public string title; public bool missing; public ImageInfo[] imageinfo; }
        [System.Serializable] class ImageInfo { public string url, thumburl; }

        [MenuItem("IndustryPlanner/Download Satisfactory icons")]
        static void Download()
        {
            if (!EditorUtility.DisplayDialog("Download Satisfactory icons",
                    "Downloads item and building images from satisfactory.wiki.gg into " + IconRoot + ". " +
                    "Files that already exist are kept.\n\nThe artwork belongs to Coffee Stain Studios: fine for your own use, " +
                    "but check their fan content policy before you distribute the app with it.", "Download", "Cancel"))
                return;

            GameDef game = JsonUtility.FromJson<GameDef>(File.ReadAllText(GameFile));
            var wanted = game.items.Select(i => (i.id, i.name, folder: "Items"))
                .Concat(game.buildings.Where(b => !b.id.StartsWith("IP_"))
                    .Select(b => (b.id, name: WikiNames.TryGetValue(b.id, out string wiki) ? wiki : b.name, folder: "Buildings")))
                .Where(w => !File.Exists(PathFor(w.folder, w.id)))
                .ToList();

            var missing = new List<string>();
            int saved = 0;
            try
            {
                // 1. Resolve image URLs, 50 titles per request.
                var urls = new Dictionary<string, string>();
                for (int i = 0; i < wanted.Count; i += BatchSize)
                {
                    EditorUtility.DisplayProgressBar("Download icons", "Looking up images on the wiki", (float)i / wanted.Count);
                    string titles = string.Join("|", wanted.Skip(i).Take(BatchSize).Select(w => FileTitle(w.name)));
                    using UnityWebRequest lookup = Get($"{Api}?action=query&format=json&formatversion=2&prop=imageinfo" +
                                                       $"&iiprop=url&iiurlwidth=128&titles={UnityWebRequest.EscapeURL(titles)}");
                    string text = lookup?.downloadHandler.text;
                    var pages = text != null ? JsonUtility.FromJson<ApiResponse>(text)?.query?.pages : null;
                    if (pages == null) continue;
                    foreach (Page p in pages)
                        if (!p.missing && p.imageinfo is { Length: > 0 })
                            urls[p.title] = p.imageinfo[0].thumburl ?? p.imageinfo[0].url;
                }

                // 2. Download each image.
                for (int i = 0; i < wanted.Count; i++)
                {
                    var (id, name, folder) = wanted[i];
                    if (EditorUtility.DisplayCancelableProgressBar("Download icons", name, (float)i / wanted.Count)) break;
                    if (!urls.TryGetValue(FileTitle(name), out string url)) { missing.Add($"{name} ({id})"); continue; }

                    using UnityWebRequest r = Get(url);
                    if (r == null) { missing.Add($"{name} ({id})"); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(PathFor(folder, id)));
                    File.WriteAllBytes(PathFor(folder, id), r.downloadHandler.data);
                    saved++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }

            Debug.Log($"Saved {saved} icons to {IconRoot}." + (missing.Count > 0
                ? $" Not found on the wiki ({missing.Count}), add these by hand:\n" + string.Join("\n", missing)
                : ""));
        }

        /// <summary>Wiki file pages use the in-game name: "Iron Plate" -> "File:Iron Plate.png".</summary>
        static string FileTitle(string name) => $"File:{name}.png";

        static string PathFor(string folder, string id) => $"{IconRoot}/{folder}/{id}.png";

        /// <summary>Blocking GET; fine for a one-off editor command. Returns null on failure.</summary>
        static UnityWebRequest Get(string url)
        {
            var request = UnityWebRequest.Get(url);
            request.SetRequestHeader("User-Agent", "IndustryPlanner/0.0.1 (personal production planner)");
            UnityWebRequestAsyncOperation op = request.SendWebRequest();
            while (!op.isDone) Thread.Sleep(10);
            if (request.result == UnityWebRequest.Result.Success) return request;
            Debug.LogWarning($"Request failed ({request.error}): {url}");
            request.Dispose();
            return null;
        }
    }
}
