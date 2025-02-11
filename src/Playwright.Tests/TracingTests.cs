/*
 * MIT License
 *
 * Copyright (c) Microsoft Corporation.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace Microsoft.Playwright.Tests
{
    ///<playwright-file>tracing.spec.ts</playwright-file>
    public class TracingTests : ContextTestEx
    {
        [PlaywrightTest("tracing.spec.ts", "should collect trace with resources, but no js")]
        public async Task ShouldCollectTrace()
        {
            await Context.Tracing.StartAsync(new()
            {
                Name = "test",
                Screenshots = true,
                Snapshots = true
            });

            var page = await Context.NewPageAsync();
            await page.GotoAsync(Server.Prefix + "/frames/frame.html");
            await page.SetContentAsync("<button>Click</button>");
            await page.ClickAsync("\"Click\"");
            await page.WaitForTimeoutAsync(2000);
            await page.CloseAsync();

            using var tmp = new TempDirectory();
            var tracePath = Path.Combine(tmp.Path, "trace.zip");
            await Context.Tracing.StopAsync(new() { Path = tracePath });

            var (events, resources) = ParseTrace(tracePath);
            CollectionAssert.IsNotEmpty(events);

            Assert.AreEqual("context-options", events[0].Type);

            Assert.GreaterOrEqual(events.Where(x => x.ApiName == "frame.goto").Count(), 1);
            Assert.GreaterOrEqual(events.Where(x => x.ApiName == "frame.setContent").Count(), 1);
            Assert.GreaterOrEqual(events.Where(x => x.ApiName == "frame.click").Count(), 1);
            Assert.GreaterOrEqual(events.Where(x => x.ApiName == "page.close").Count(), 1);

            Assert.GreaterOrEqual(events.Where(x => x.Type == "frame-snapshot").Count(), 1);
            Assert.GreaterOrEqual(events.Where(x => x.Type == "resource-snapshot").Count(), 1);
            Assert.GreaterOrEqual(events.Where(x => x.Type == "screencast-frame").Count(), 1);
        }

        [PlaywrightTest("tracing.spec.ts", "should collect two traces")]
        public async Task ShouldCollectTwoTraces()
        {
            var page = await Context.NewPageAsync();
            await Context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true });
            await page.GotoAsync(Server.EmptyPage);
            await page.SetContentAsync("<button>Click</button>");
            await page.ClickAsync("\"Click\"");

            using var tmp = new TempDirectory();
            var trace1Path = Path.Combine(tmp.Path, "trace1.zip");
            await Context.Tracing.StopAsync(new() { Path = trace1Path });

            await Context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true });
            await page.DblClickAsync("\"Click\"");
            await page.CloseAsync();
            var trace2Path = Path.Combine(tmp.Path, "trace2.zip");
            await Context.Tracing.StopAsync(new() { Path = trace2Path });

            {
                var (events, resources) = ParseTrace(trace1Path);
                Assert.AreEqual("context-options", events[0].Type);
                Assert.GreaterOrEqual(events.Where(x => x.ApiName == "frame.goto").Count(), 1);
                Assert.GreaterOrEqual(events.Where(x => x.ApiName == "frame.setContent").Count(), 1);
                Assert.GreaterOrEqual(events.Where(x => x.ApiName == "frame.click").Count(), 1);
                Assert.AreEqual(0, events.Where(x => x.ApiName == "page.close").Count());
                Assert.AreEqual(0, events.Where(x => x.ApiName?.Contains("dblClick") ?? false).Count());
            }

            {
                var (events, resources) = ParseTrace(trace2Path);
                Assert.AreEqual("context-options", events[0].Type);
                Assert.AreEqual(0, events.Where(x => x.ApiName == "frame.goto").Count());
                Assert.AreEqual(0, events.Where(x => x.ApiName == "frame.setContent").Count());
                Assert.AreEqual(0, events.Where(x => x.ApiName == "frame.click").Count());
                Assert.GreaterOrEqual(events.Where(x => x.ApiName == "page.close").Count(), 1);
                Assert.GreaterOrEqual(events.Where(x => x.ApiName?.Contains("dblclick") ?? false).Count(), 1);
            }

        }

        [PlaywrightTest("tracing.spec.ts", "should collect sources")]
        public async Task ShouldCollectSources()
        {
            await Context.Tracing.StartAsync(new()
            {
                Sources = true,
            });

            var page = await Context.NewPageAsync();
            await page.GotoAsync(Server.Prefix + "/empty.html");
            await page.SetContentAsync("<button>Click</button>");
            await page.ClickAsync("\"Click\"");
            await page.CloseAsync();

            using var tmp = new TempDirectory();
            var tracePath = Path.Combine(tmp.Path, "trace.zip");
            await Context.Tracing.StopAsync(new() { Path = tracePath });

            var (events, resources) = ParseTrace(tracePath);
            var sourceNames = resources.Keys.Where(key => key.EndsWith(".txt")).ToArray();
            Assert.AreEqual(sourceNames.Count(), 1);
            var sourceTraceFileContent = resources[sourceNames[0]];
            var currentFileContent = File.ReadAllText(new StackTrace(true).GetFrame(0).GetFileName());

            Assert.AreEqual(sourceTraceFileContent, currentFileContent);
        }

        private static (IReadOnlyList<TraceEventEntry> Events, Dictionary<string, byte[]> Resources) ParseTrace(string path)
        {
            Dictionary<string, byte[]> resources = new();
            var archive = ZipFile.OpenRead(path);
            foreach (var entry in archive.Entries)
            {
                var memoryStream = new MemoryStream();
                entry.Open().CopyTo(memoryStream);
                resources.Add(entry.Name, memoryStream.ToArray());
            }
            List<TraceEventEntry> events = new();
            foreach (var fileName in new[] { "trace.trace", "trace.network" })
            {
                foreach (var line in Encoding.UTF8.GetString(resources[fileName]).Split("\n"))
                {
                    if (!string.IsNullOrEmpty(line))
                    {
                        events.Add(JsonSerializer.Deserialize<TraceEventEntry>(line,
                                new JsonSerializerOptions()
                                {
                                    PropertyNameCaseInsensitive = true,
                                }));
                    }
                }
            }
            return (events, resources);
        }

        private class TraceEventEntry
        {
            public string Type { get; set; }

            public TraceEventMetadata Metadata { get; set; }

            [JsonIgnore]
            public string ApiName { get => Metadata != null ? $"{Metadata.Type.ToLower()}.{Metadata.Method}" : null; }
        }

        private class TraceEventMetadata
        {
            public string Type { get; set; }

            public string Method { get; set; }

            public string PageId { get; set; }
        }
    }
}
