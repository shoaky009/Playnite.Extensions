using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Extensions.Common;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace DLSiteMetadata;

public class Scrapper
{
    public const string DefaultLanguage = "en_US";

    public const string SiteBaseUrl = "https://www.dlsite.com/";
    public const string ProductBaseUrl = SiteBaseUrl + "maniax/work/=/product_id/";

    public static string SearchFormatUrl = SiteBaseUrl + BuildSearchUrl();
    static readonly HttpClient httpclient = new();

    public static string BuildSearchUrl()
    {
        var builder = new StringBuilder("maniax/fsr/=/language/jp/sex_category%5B0%5D/male/keyword/{0}");
        builder.Append("/order%5B0%5D/trend");
        builder.Append("/work_category%5B0%5D/pc");
        builder.Append("/work_type%5B0%5D/ACN");
        builder.Append("/work_type%5B1%5D/QIZ");
        builder.Append("/work_type%5B2%5D/ADV");
        builder.Append("/work_type%5B3%5D/RPG");
        builder.Append("/work_type%5B4%5D/TBL");
        builder.Append("/work_type%5B5%5D/SLN");
        builder.Append("/work_type%5B6%5D/TYP");
        builder.Append("/work_type%5B7%5D/STG");
        builder.Append("/work_type%5B8%5D/PZL");
        builder.Append("/work_type%5B9%5D/ETC");
        builder.Append("/per_page/{1}");
        builder.Append("/from/fs.header/");
        builder.Append("?locale={2}");
        return builder.ToString();
    }

    private static readonly Regex _idRegex = new Regex(@"[a-zA-Z]+[0-9]+", RegexOptions.Compiled);

    private static readonly Regex _imageLinkRegex =
        new Regex(@"(https?://[a-zA-Z0-9\.\?/%-_]*).(jpg|jpeg|png)", RegexOptions.Compiled);

    private readonly ILogger<Scrapper> _logger;
    private readonly IConfiguration _configuration;

    public Scrapper(ILogger<Scrapper> logger, HttpMessageHandler messageHandler)
    {
        _logger = logger;

        _configuration = Configuration.Default
            .WithRequesters(messageHandler)
            .WithDefaultLoader();
    }

    public async Task<ScrapperResult> ScrapGamePage(string url, CancellationToken cancellationToken = default,
        string language = DefaultLanguage)
    {
        if (!url.Contains("/?locale="))
        {
            url += url.EndsWith("/") ? $"?locale={language}" : $"/?locale={language}";
        }

        var res = new ScrapperResult
        {
            Link = url
        };

        var idMatcher = _idRegex.Match(url);
        HttpClient client = new HttpClient();
        var ajaxResult =
            await client.GetAsync($"https://www.dlsite.com/maniax/product/info/ajax?product_id={idMatcher.Value}",
                cancellationToken);
        var ajaxText = await ajaxResult.Content.ReadAsStringAsync();

        JsonReader reader = new JsonTextReader(new StringReader(ajaxText));
        double score = 0;
        while (reader.Read())
        {
            if (reader.Path.EndsWith("rate_average_2dp"))
            {
                score = reader.ReadAsDouble() ?? 0;
            }
        }

        res.Score = (int)(score * 20);

        var context = BrowsingContext.New(_configuration);
        var document = await context.OpenAsync(url, cancellationToken);

        // Title
        var itemThingElement = document.GetElementsByClassName("topicpath_item").LastOrDefault();
        if (itemThingElement is not null)
        {
            res.Title = itemThingElement.Children.FirstOrDefault()?.Children.FirstOrDefault()?.Text().Trim();
        }

        // Maker
        var makerNameElement = document.GetElementsByClassName("maker_name").FirstOrDefault();
        var makerNameAnchorElement =
            (IHtmlAnchorElement?)makerNameElement?.Children.FirstOrDefault(x =>
                x.TagName.Equals(TagNames.A, StringComparison.OrdinalIgnoreCase));
        if (makerNameAnchorElement is not null)
        {
            res.Maker = makerNameAnchorElement.Text().Trim();
        }

        if (res.Maker == null)
        {
            res.Maker = makerNameElement?.Text();
        }

        var addFollowElement = document.GetElementsByClassName("add_follow").FirstOrDefault();
        if (addFollowElement is not null)
        {
            res.Maker = addFollowElement.GetAttribute("data-follow-name");
        }

        var descriptionElements = document.GetElementsByClassName("work_parts_container").FirstOrDefault()?.Children;
        var descriptionHtml = "";
        if (descriptionElements is not null)
        {
            foreach (var element in descriptionElements)
            {
                if (element.ClassName == "work_parts type_chobit")
                {
                    continue;
                }

                var newHtml = element.InnerHtml.Replace("<img src=\"//", "<img src=\"https://");
                newHtml = newHtml.Replace("<a href=\"//", "<a href=\"https://");
                descriptionHtml += newHtml;
            }
        }

        res.DescriptionHtml = descriptionHtml;

        var imageMatches = _imageLinkRegex.Matches(descriptionHtml);
        var imageListInDescription = new List<string>();
        foreach (Match image in imageMatches)
        {
            imageListInDescription.Add(image.Value);
        }

        // Images
        var productSliderDataElement = document.GetElementsByClassName("product-slider-data").FirstOrDefault();
        if (productSliderDataElement is not null)
        {
            res.ProductImages = productSliderDataElement.Children
                .Where(x => x.TagName.Equals(TagNames.Div, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.HasAttribute("data-src"))
                // .Select(x => x.GetAttribute("data-src")!.Trim().Replace(".jpg", ".webp"))
                .Select(x => x.GetAttribute("data-src")!.Trim())
                .Select(x => !x.StartsWith("//") ? x : $"https:{x}")
                .Concat(imageListInDescription.AsEnumerable())
                .Distinct()
                .ToList();
        }

        var workOutlineTable = document.QuerySelector("#work_outline");
        if (workOutlineTable is not null)
        {
            var tableRows = workOutlineTable.Children.FirstOrDefault()?.Children;
            if (tableRows is not null && tableRows.Any())
            {
                foreach (var tableRow in tableRows)
                {
                    var headerName = tableRow.Children
                        .FirstOrDefault(x => x.TagName.Equals(TagNames.Th, StringComparison.OrdinalIgnoreCase))?.Text()
                        .Trim();
                    var dataElement = tableRow.Children.FirstOrDefault(x =>
                        x.TagName.Equals(TagNames.Td, StringComparison.OrdinalIgnoreCase));
                    if (headerName is not null && dataElement is not null)
                    {
                        if (headerName.Equals("Release date", StringComparison.OrdinalIgnoreCase))
                        {
                            var sDate = dataElement.Text().CustomTrim();
                            if (DateTime.TryParseExact(sDate, "MMM/dd/yyyy", null, DateTimeStyles.None,
                                    out var dateReleased))
                            {
                                res.DateReleased = dateReleased;
                            }
                        }
                        else if (headerName.Equals("販売日", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("贩卖日", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("販賣日", StringComparison.OrdinalIgnoreCase))
                        {
                            var sDate = dataElement.Text().CustomTrim();
                            if (DateTime.TryParseExact(sDate, "yyyy年MM月dd日", null, DateTimeStyles.None,
                                    out var dateReleased))
                            {
                                res.DateReleased = dateReleased;
                            }
                        }
                        else if (headerName.Equals("판매일", StringComparison.OrdinalIgnoreCase))
                        {
                            var sDate = dataElement.Text().CustomTrim();
                            if (DateTime.TryParseExact(sDate, "yyyy년 MM월 dd일", null, DateTimeStyles.None,
                                    out var dateReleased))
                            {
                                res.DateReleased = dateReleased;
                            }
                        }
                        else if (headerName.Equals("Update information", StringComparison.OrdinalIgnoreCase))
                        {
                            var sDate = dataElement.Text().CustomTrim().Substring(0, 11);
                            if (DateTime.TryParseExact(sDate, "MMM/dd/yyyy", null, DateTimeStyles.None,
                                    out var dateUpdated))
                            {
                                res.DateUpdated = dateUpdated;
                            }
                        }
                        else if (headerName.Equals("更新情報", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("更新信息", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("更新資訊", StringComparison.OrdinalIgnoreCase))
                        {
                            var sDate = dataElement.Text().CustomTrim().Substring(0, 11);
                            if (DateTime.TryParseExact(sDate, "yyyy年MM月dd日", null, DateTimeStyles.None,
                                    out var dateUpdated))
                            {
                                res.DateUpdated = dateUpdated;
                            }
                        }
                        else if (headerName.Equals("갱신 정보", StringComparison.OrdinalIgnoreCase))
                        {
                            var sDate = dataElement.Text().CustomTrim().Substring(0, 11);
                            if (DateTime.TryParseExact(sDate, "yyyy년 MM월 dd일", null, DateTimeStyles.None,
                                    out var dateUpdated))
                            {
                                res.DateUpdated = dateUpdated;
                            }
                        }
                        else if (headerName.Equals("Series name", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("シリーズ名", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("系列名", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("시리즈명", StringComparison.OrdinalIgnoreCase))
                        {
                            res.SeriesNames = dataElement.Text().CustomTrim();
                        }
                        else if (headerName.Equals("Scenario", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("シナリオ", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("剧情", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("劇本", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("시나리오", StringComparison.OrdinalIgnoreCase))
                        {
                            res.ScenarioWriters = dataElement.Children
                                .Where(x => x.TagName.Equals(TagNames.A, StringComparison.OrdinalIgnoreCase))
                                .Select(x => x.Text().CustomTrim())
                                .ToList();
                        }
                        else if (headerName.Equals("Illustration", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("イラスト", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("插画", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("插畫", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("일러스트", StringComparison.OrdinalIgnoreCase))
                        {
                            res.Illustrators = dataElement.Children
                                .Where(x => x.TagName.Equals(TagNames.A, StringComparison.OrdinalIgnoreCase))
                                .Select(x => x.Text().CustomTrim())
                                .ToList();
                        }
                        else if (headerName.Equals("Author", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("作者", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("作者", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("저자", StringComparison.OrdinalIgnoreCase))
                        {
                            res.Author = dataElement.Text().CustomTrim();
                        }
                        else if (headerName.Equals("Voice Actor", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("声優", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("声优", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("聲優", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("성우", StringComparison.OrdinalIgnoreCase))
                        {
                            res.VoiceActors = dataElement.Children
                                .Where(x => x.TagName.Equals(TagNames.A, StringComparison.OrdinalIgnoreCase))
                                .Select(x => x.Text().CustomTrim())
                                .ToList();
                        }
                        else if (headerName.Equals("Music", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("音楽", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("音乐", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("音樂", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("음악", StringComparison.OrdinalIgnoreCase))
                        {
                            res.MusicCreators = dataElement.Children
                                .Where(x => x.TagName.Equals(TagNames.A, StringComparison.OrdinalIgnoreCase))
                                .Select(x => x.Text().CustomTrim())
                                .ToList();
                        }
                        else if (headerName.Equals("Age", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("年齢指定", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("年龄指定", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("年齡指定", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("연령 지정", StringComparison.OrdinalIgnoreCase))
                        {
                            res.AgeRating = dataElement.QuerySelector(".work_genre span")?.Text().Trim();
                        }
                        else if (headerName.Equals("Product format", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("作品形式", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("作品类型", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("작품 형식", StringComparison.OrdinalIgnoreCase))
                        {
                            res.Categories = dataElement.Children.First().Children
                                .Where(x => x.TagName.Equals(TagNames.A, StringComparison.OrdinalIgnoreCase))
                                .Select(x => x.Text().CustomTrim())
                                .ToList();

                            var additionalInfoElement = dataElement.Children.First().Children
                                .FirstOrDefault(x => x.ClassList.Contains("additional_info"));
                            if (additionalInfoElement is not null)
                            {
                                res.Categories.Add(additionalInfoElement.Text().Replace('/', ' ').CustomTrim());
                            }
                        }
                        else if (headerName.Equals("File format", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("ファイル形式", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("文件形式", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("檔案形式", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("파일 형식", StringComparison.OrdinalIgnoreCase))
                        {
                            // TODO:
                        }
                        else if (headerName.Equals("Supported languages", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("対応言語", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("对应语言", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("支持的语言", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("對應語言", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("대응 언어", StringComparison.OrdinalIgnoreCase))
                        {
                            // TODO:
                        }
                        else if (headerName.Equals("Genre", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("ジャンル", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("分类", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("分類", StringComparison.OrdinalIgnoreCase) ||
                                 headerName.Equals("장르", StringComparison.OrdinalIgnoreCase))
                        {
                            res.Genres = dataElement.Children.First().Children
                                .Where(x => x.TagName.Equals(TagNames.A, StringComparison.OrdinalIgnoreCase))
                                .Select(x => x.Text().CustomTrim())
                                .ToList();
                        }
                        else if (headerName.Equals("File size", StringComparison.OrdinalIgnoreCase))
                        {
                            //"Total 4.82GB"
                            // res.FileSize = dataElement.Text().CustomTrim().Substring(6);
                        }
                        else if (headerName.Equals("ファイル容量", StringComparison.OrdinalIgnoreCase))
                        {
                            //"総計 4.82GB"
                            // res.FileSize = dataElement.Text().CustomTrim().Substring(3);
                        }
                        else
                        {
                            _logger.LogWarning("Unknown header: \"{HeaderName}\"", headerName);
                        }
                    }
                }
            }
        }

        // https://img.dlsite.jp/modpub/images2/work/doujin/RJ247000/RJ246037_img_main.jpg
        // https://img.dlsite.jp/modpub/images2/work/doujin/RJ247000/RJ246037_img_sam_mini.jpg

        if (res.ProductImages is not null && res.ProductImages.Any())
        {
            var mainImage = res.ProductImages.FirstOrDefault(x => x.Contains("_img_main."));
            if (mainImage is not null)
            {
                res.Cover = mainImage;
                res.Icon = mainImage.Replace("_img_main.", "_img_sam_mini.");
                ;
            }
        }

        return res;
    }

    public async Task<List<SearchResult>> ScrapSearchPage(string term, CancellationToken cancellationToken = default,
        int maxResults = 50, string language = DefaultLanguage)
    {
        var context = BrowsingContext.New(_configuration);

        var url = string.Format(SearchFormatUrl, term, maxResults, language);
        var document = await context.OpenAsync(url, cancellationToken);

        var results = new List<SearchResult>();

        var resultListElement = document.GetElementsByClassName("n_worklist")
            .FirstOrDefault(x => x.TagName.Equals(TagNames.Ul, StringComparison.OrdinalIgnoreCase));

        if (resultListElement is not null)
        {
            var enumerable = resultListElement.Children
                .Where(x => x.TagName.Equals(TagNames.Li, StringComparison.OrdinalIgnoreCase));

            foreach (var elem in enumerable)
            {
                var divElement = elem.GetElementsByClassName("multiline_truncate").FirstOrDefault();
                if (divElement is null) continue;

                var anchorElement = (IHtmlAnchorElement?)divElement.Children.FirstOrDefault(x =>
                    x.TagName.Equals(TagNames.A, StringComparison.OrdinalIgnoreCase));
                if (anchorElement is null) continue;

                var title = anchorElement.Title;
                var href = anchorElement.Href;

                if (title is null) continue;
                results.Add(new SearchResult(title, href));
            }
        }

        if (results.Count == 0)
        {
            // use suggestion api much better results
            var time = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            url = $"https://www.dlsite.com/suggest/?term={term}&site=adult-jp&time={time}";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"locale={language};adultchecked=1");
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3");
            var response = await httpclient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync();
            if (body == null)
            {
                return results;
            }

            var suggestions = JsonConvert.DeserializeObject<SuggestionResult>(body);
            suggestions.Work
                .ForEach(work =>
                {
                    var workNo = work.WorkNo;
                    var suggestionUrl = $"https://www.dlsite.com/maniax/work/=/product_id/{workNo}.html";
                    if (work.IsAna == true)
                    {
                        suggestionUrl = $"https://www.dlsite.com/soft/announce/=/product_id/{workNo}.html";
                    }
                    else if (workNo?.StartsWith("VJ") == true)
                    {
                        suggestionUrl = $"https://www.dlsite.com/soft/work/=/product_id/{workNo}.html";
                    }
                    var searchItem = new SearchResult(work.WorkName,
                        suggestionUrl
                    );
                    results.Add(searchItem);
                });
        }

        return results;
    }

    private struct SuggestionItem
    {
        [JsonProperty("work_name")] public string WorkName { get; set; }
        [JsonProperty("workno")] public string? WorkNo { get; set; }
        [JsonProperty("work_type")] public string? WorkType { get; set; }
        [JsonProperty("is_ana")] public bool? IsAna { get; set; }
    }

    private struct SuggestionResult
    {
        [JsonProperty("work")] public List<SuggestionItem> Work { get; set; }
    }
}
