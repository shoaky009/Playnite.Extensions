using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Common;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace FanzaMetadata;

public class FanzaGameScrapper : IScrapper
{
    private const string BaseSearchUrl = "https://dlsoft.dmm.co.jp/search?service=pcgame&searchstr=";
    private const string BaseGamePageUrl = "https://dlsoft.dmm.co.jp/detail/";
    private const string PosterUrlPattern = "https://pics.dmm.co.jp/digital/pcgame/{0}/{0}pl.jpg";

    private const string BaseMonoSearchUrl = "https://www.dmm.co.jp/search/=/searchstr={0}/limit=30/sort=rankprofile/";
    private const string BaseMonoGamePageUrl = "https://www.dmm.co.jp/mono/pcgame/-/detail/=/cid=";

    private readonly ILogger<FanzaGameScrapper> _logger;
    private readonly IConfiguration _configuration;

    public FanzaGameScrapper(ILogger<FanzaGameScrapper> logger)
    {
        _logger = logger;
        var clientHandler = new HttpClientHandler();
        clientHandler.Properties.Add("User-Agent", "Playnite.Extensions");
        var cookieContainer = new CookieContainer();
        cookieContainer.Add(new Cookie("age_check_done", "1", "/", ".dmm.co.jp")
        {
            Expires = DateTime.Now + TimeSpan.FromDays(30),
            HttpOnly = true
        });

        clientHandler.CookieContainer = cookieContainer;
        _configuration = Configuration.Default.WithRequesters(clientHandler).WithDefaultLoader();
        clientHandler.UseCookies = true;
    }

    public FanzaGameScrapper(ILogger<FanzaGameScrapper> logger, HttpClientHandler messageHandler)
    {
        _logger = logger;
        _configuration = Configuration.Default.WithRequesters(messageHandler).WithDefaultLoader();
        messageHandler.UseCookies = true;
    }


    public async Task<List<SearchResult>> ScrapSearchPage(string searchName,
        CancellationToken cancellationToken = default)
    {
        var url = BaseSearchUrl + Uri.EscapeUriString(searchName);
        var context = BrowsingContext.New(_configuration);
        var document = await context.OpenAsync(url, cancellationToken);

        await Console.Out.WriteAsync(document.Title);

        var dlResults = document
            .QuerySelectorAll(".component-legacy-productTile .component-legacy-productTile__detailLink")
            .Where(element => !string.IsNullOrEmpty(element.HyperReference("")?.Href))
            .Select(element =>
                {
                    var title = element.GetElementsByClassName("component-legacy-productTile__title").First().Text();
                    var anchorElement = (IHtmlAnchorElement)element;
                    var href = anchorElement.Href;
                    var id = new Uri(href).Segments.Last().Replace("/", "");
                    return new SearchResult(title, id, href);
                }
            ).ToList();
        if (dlResults.Count > 0)
        {
            return dlResults;
        }

        var monoSearchUrl = string.Format(BaseMonoSearchUrl, searchName);
        context = BrowsingContext.New(_configuration);
        document = await context.OpenAsync(monoSearchUrl, cancellationToken);

        return document.QuerySelectorAll("#list li")
            .Where(element =>
            {
                var aElement = (IHtmlAnchorElement)element.QuerySelectorAll(".tmb a").First();
                return aElement.Href.Contains("/mono/pcgame");
            })
            .Select(element =>
            {
                var aElement = (IHtmlAnchorElement)element.QuerySelectorAll(".tmb a").First();
                var title = aElement.QuerySelectorAll("img").First().GetAttribute("alt") + "";
                var id = new Uri(aElement.Href).Segments
                    .Where(x => x.StartsWith("cid="))
                    .Select(x => x.ReplaceFirst("cid=", "").Replace("/", ""))
                    .First() + "";
                return new SearchResult(title, id, aElement.Href);
            }).ToList();
    }


    public async Task<ScrapperResult?> ScrapGamePage(SearchResult searchResult,
        CancellationToken cancellationToken = default)
    {
        return await ScrapGamePage(searchResult.Href, cancellationToken);
    }

    public async Task<ScrapperResult?> ScrapGamePage(string link, CancellationToken cancellationToken)
    {
        var id = GetGameIdFromLinks(new List<string> { link });
        if (string.IsNullOrEmpty(id)) return null;

        var context = BrowsingContext.New(_configuration);
        var document = await context.OpenAsync(link, cancellationToken);
        if (document.StatusCode == HttpStatusCode.NotFound) return null;

        var result = new ScrapperResult
        {
            Link = link
        };
        if (link.StartsWith(BaseGamePageUrl, StringComparison.OrdinalIgnoreCase))
        {
            result.Title = document.QuerySelector(".productTitle__item")?.Text().Trim();
            var detailTop = document.QuerySelectorAll(".contentsDetailTop__tableRow")
                .GroupBy(x => x.Children.First().Text().Trim())
                .ToDictionary(x => x.Key, x => x.First().Children.Last().Text().Trim());
            result.Circle = detailTop["ブランド"];

            result.PreviewImages = document.GetElementsByClassName("image-slider").Children("li").Children("img")
                .Cast<IHtmlImageElement>().Select(x => x.Source ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();

            var rating = document.QuerySelectorAll("script[type='application/ld+json']")
                .Select(x =>
                    JsonSerializer.Create()
                        .Deserialize<Product>(new JsonTextReader(new StringReader(x.Text()))))
                .Where(x => x?.Type?.Equals("Product") == true)
                .Select(x => x.AggregateRating?.RatingValue)
                .First()?.ToDouble();
            if (rating != null)
            {
                result.Rating = rating.Value;
            }

            // const string ratingPrefix = "d-rating-";
            // result.Rating = document.QuerySelector(".review div")!.ClassList
            //     .Where(className => className.StartsWith(ratingPrefix))
            //     .Select(className => className.Replace(ratingPrefix, ""))
            //     .Select(rating => double.Parse(rating) / 10D).First();

            result.Description = document.GetElementById("detailGuide")?.OuterHtml.Trim();

            var r18NavigationBarLength = document.GetElementsByClassName("_n4v1-link-r18-name").Length;
            result.Adult = r18NavigationBarLength <= 0;

            var detailBottom = document.QuerySelectorAll(".contentsDetailBottom__tableRow")
                .GroupBy(x => x.Children.First().Text().Trim())
                .ToDictionary(x => x.Key, x => x.First().Children.Last());

            var dateKey = detailBottom.Keys.First(x => x.Contains("配信"));
            var dateStr = detailBottom.GetOrDefault(dateKey, null)?.Text().Trim().SplitSpaces().First();
            if (DateTime.TryParseExact(dateStr, "yyyy/MM/dd", null, DateTimeStyles.None, out var releaseDate))
            {
                result.ReleaseDate = releaseDate;
            }

            const string noneVal = "----";
            var gameGenre = detailBottom["ゲームジャンル"]?.Text().Trim();
            if (!noneVal.Equals(gameGenre))
            {
                result.GameGenre = gameGenre;
            }

            var series = detailBottom["シリーズ"]?.Text().Trim();
            if (!noneVal.Equals(series))
            {
                result.Series = series;
            }

            var tags = detailBottom["ジャンル"]?.GetElementsByTagName("a")
                .Select(x => x.Text().Trim())
                .Where(x => !x.Contains("感謝祭"))
                .ToList();
            result.Genres = tags;

            result.CoverUrl = string.Format(PosterUrlPattern, id);

            if (detailBottom.ContainsKey("原画"))
            {
                result.Illustrators = detailBottom["原画"]
                    .QuerySelectorAll("li a")
                    .Select(x => x.Text().Trim()).ToList();
            }

            if (detailBottom.ContainsKey("シナリオ"))
            {
                result.ScenarioWriters = detailBottom["シナリオ"]
                    .QuerySelectorAll("li a")
                    .Select(x => x.Text().Trim()).ToList();
            }

            if (detailBottom.ContainsKey("声優"))
            {
                result.VoiceActors = detailBottom["声優"]
                    .QuerySelectorAll("li")
                    .Select(x => x.Text().Trim()).ToList();
            }
        }
        else
        {
            // MONO page
            result.Title = document.GetElementById("title")?.Text().Trim();
            // css
            var productDetailDict = document.QuerySelectorAll(".wrapper-detailContents .wrapper-product table tr")
                .Select(ele =>
                {
                    var key = ele.FirstElementChild?.Text().Replace("：", "").Trim();
                    var value = ele.LastElementChild;
                    return new KeyValuePair<string, IElement>(key!, value!);
                })
                .Where(pair => !string.IsNullOrEmpty(pair.Key))
                .ToDictionary(ele => ele.Key, ele => ele.Value);

            result.Circle = productDetailDict["ブランド"]?.Text().Trim();
            result.PreviewImages = document.QuerySelectorAll("#sample-image-block a img")
                .Cast<IHtmlImageElement>()
                .Select(x => x.GetAttribute("data-lazy") ?? "")
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x.ReplaceFirst("js-", "jp-"))
                .ToList();

            result.Description = document.QuerySelector("div.page-detail div.mg-b20.lh4 p.mg-b20")?.OuterHtml.Trim();

            var rating = productDetailDict["平均評価"]?.QuerySelectorAll("img")
                .Cast<IHtmlImageElement>()
                .Select(x => x.Source)
                .Where(x => x?.EndsWith(".gif") ?? false)
                .Select(x =>
                {
                    var str = x + "";
                    var idx = str.LastIndexOf("/", StringComparison.Ordinal);
                    if (idx < 0)
                    {
                        return -1D;
                    }

                    var dd = str.Substring(idx + 1).Replace(".gif", "");
                    return dd.ToDouble();
                })
                .FirstOrDefault(x => x > 0) ?? 0D;
            result.Rating = rating / 10D;

            var r18NavigationBarLength = document.GetElementsByClassName("_n4v1-link-r18-name").Length;
            result.Adult = r18NavigationBarLength <= 0;

            if (productDetailDict.ContainsKey("発売日"))
            {
                var dateStr = productDetailDict["発売日"]?.Text().Trim();
                if (DateTime.TryParseExact(dateStr, "yyyy/MM/dd", null, DateTimeStyles.None, out var releaseDate))
                {
                    result.ReleaseDate = releaseDate;
                }
            }


            if (productDetailDict.ContainsKey("ゲームジャンル"))
            {
                result.GameGenre = productDetailDict["ゲームジャンル"]?.Text().Trim();
            }

            if (productDetailDict.ContainsKey("シリーズ"))
            {
                result.Series = productDetailDict["シリーズ"]?.Text().Trim();
            }


            if (productDetailDict.ContainsKey("ジャンル"))
            {
                var tags = productDetailDict["ジャンル"]?.QuerySelectorAll("td a")
                    .Select(x => x.Text().Replace("#", "").Trim()).ToList();
                result.Genres = tags;
            }

            result.CoverUrl = string.Format("https://pics.dmm.co.jp/mono/game/{0}/{0}pl.jpg", id);

            if (productDetailDict.ContainsKey("原画"))
            {
                result.Illustrators = productDetailDict["原画"]
                    .QuerySelectorAll("td a")
                    .Select(x => x.Text().Trim()).ToList();
            }

            if (productDetailDict.ContainsKey("シナリオ"))
            {
                result.ScenarioWriters = productDetailDict["シナリオ"]
                    .QuerySelectorAll("td a")
                    .Select(x => x.Text().Trim()).ToList();
            }

            // if (productDetailDict.ContainsKey("ボイス"))
            // {
            //     result.VoiceActors = productDetailDict["ボイス"]
            //         .QuerySelectorAll("td")
            //         .Select(x => x.Text().Trim()).ToList();
            // }
        }

        return result;
    }

    public static string? ParseLinkId(string? link)
    {
        if (link == null) return null;
        return new Uri(link).Segments.Last().Replace("/", "").Replace("cid=", "");
    }

    public string? GetGameIdFromLinks(IEnumerable<string> links)
    {
        return links
            .Where(link =>
                link.StartsWith(BaseGamePageUrl, StringComparison.OrdinalIgnoreCase)
                || link.StartsWith(BaseMonoGamePageUrl, StringComparison.OrdinalIgnoreCase))
            .Select(ParseLinkId).FirstOrDefault(x => !string.IsNullOrEmpty(x));
    }

    private class Product
    {
        [JsonProperty("@type")]
        public string? Type { get; set; }
        [JsonProperty("aggregateRating")]
        public AggregateRating? AggregateRating { get; set; }
    }

    private class AggregateRating
    {
        [JsonProperty("@type")]
        public string? Type { get; set; }
        [JsonProperty("ratingValue")]
        public string? RatingValue { get; set; }
    }
}
