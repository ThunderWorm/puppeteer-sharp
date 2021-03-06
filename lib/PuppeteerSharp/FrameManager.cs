﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PuppeteerSharp.Helpers;
using PuppeteerSharp.Helpers.Json;
using PuppeteerSharp.Messaging;

namespace PuppeteerSharp
{
    internal class FrameManager
    {
        private Dictionary<int, ExecutionContext> _contextIdToContext;
        private bool _ensureNewDocumentNavigation;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, Frame> _frames;
        private const string RefererHeaderName = "referer";
        private readonly AsyncDictionaryHelper<string, Frame> _asyncFrames;
        private readonly List<string> _isolatedWorlds = new List<string>();
        private const string UtilityWorldName = "__puppeteer_utility_world__";

        private FrameManager(CDPSession client, Page page, NetworkManager networkManager, TimeoutSettings timeoutSettings)
        {
            Client = client;
            Page = page;
            _frames = new ConcurrentDictionary<string, Frame>();
            _contextIdToContext = new Dictionary<int, ExecutionContext>();
            _logger = Client.Connection.LoggerFactory.CreateLogger<FrameManager>();
            NetworkManager = networkManager;
            TimeoutSettings = timeoutSettings;
            _asyncFrames = new AsyncDictionaryHelper<string, Frame>(_frames, "Frame {0} not found");

            Client.MessageReceived += Client_MessageReceived;
        }

        #region Properties

        internal event EventHandler<FrameEventArgs> FrameAttached;
        internal event EventHandler<FrameEventArgs> FrameDetached;
        internal event EventHandler<FrameEventArgs> FrameNavigated;
        internal event EventHandler<FrameEventArgs> FrameNavigatedWithinDocument;
        internal event EventHandler<FrameEventArgs> LifecycleEvent;

        internal CDPSession Client { get; }
        internal NetworkManager NetworkManager { get; }
        internal Frame MainFrame { get; set; }
        internal Page Page { get; }
        internal TimeoutSettings TimeoutSettings { get; }
        #endregion

        #region Public Methods
        internal static async Task<FrameManager> CreateFrameManagerAsync(
            CDPSession client,
            Page page,
            NetworkManager networkManager,
            FrameTree frameTree,
            TimeoutSettings timeoutSettings)
        {
            var frameManager = new FrameManager(client, page, networkManager, timeoutSettings);
            await frameManager.HandleFrameTreeAsync(frameTree).ConfigureAwait(false);
            return frameManager;
        }

        internal ExecutionContext ExecutionContextById(int contextId)
        {
            _contextIdToContext.TryGetValue(contextId, out var context);

            if (context == null)
            {
                _logger.LogError("INTERNAL ERROR: missing context with id = {ContextId}", contextId);
            }
            return context;
        }

        public async Task<Response> NavigateFrameAsync(Frame frame, string url, NavigationOptions options)
        {
            var referrer = string.IsNullOrEmpty(options.Referer)
               ? NetworkManager.ExtraHTTPHeaders?.GetValueOrDefault(RefererHeaderName)
               : options.Referer;
            var requests = new Dictionary<string, Request>();
            var timeout = options?.Timeout ?? TimeoutSettings.NavigationTimeout;

            using (var watcher = new LifecycleWatcher(this, frame, options?.WaitUntil, timeout))
            {
                try
                {
                    var navigateTask = NavigateAsync(Client, url, referrer, frame.Id);
                    var task = await Task.WhenAny(
                        watcher.TimeoutOrTerminationTask,
                        navigateTask).ConfigureAwait(false);

                    await task;

                    task = await Task.WhenAny(
                        watcher.TimeoutOrTerminationTask,
                        _ensureNewDocumentNavigation ? watcher.NewDocumentNavigationTask : watcher.SameDocumentNavigationTask
                    ).ConfigureAwait(false);

                    await task;
                }
                catch (Exception ex)
                {
                    throw new NavigationException(ex.Message, ex);
                }

                return watcher.NavigationResponse;
            }
        }

        private async Task NavigateAsync(CDPSession client, string url, string referrer, string frameId)
        {
            var response = await client.SendAsync<PageNavigateResponse>("Page.navigate", new PageNavigateRequest
            {
                Url = url,
                Referrer = referrer ?? string.Empty,
                FrameId = frameId
            }).ConfigureAwait(false);

            _ensureNewDocumentNavigation = !string.IsNullOrEmpty(response.LoaderId);

            if (!string.IsNullOrEmpty(response.ErrorText))
            {
                throw new NavigationException(response.ErrorText, url);
            }
        }

        public async Task<Response> WaitForFrameNavigationAsync(Frame frame, NavigationOptions options = null)
        {
            var timeout = options?.Timeout ?? TimeoutSettings.NavigationTimeout;
            using (var watcher = new LifecycleWatcher(this, frame, options?.WaitUntil, timeout))
            {
                var raceTask = await Task.WhenAny(
                    watcher.NewDocumentNavigationTask,
                    watcher.SameDocumentNavigationTask,
                    watcher.TimeoutOrTerminationTask
                ).ConfigureAwait(false);

                await raceTask;

                return watcher.NavigationResponse;
            }
        }

        #endregion

        #region Private Methods

        private async void Client_MessageReceived(object sender, MessageEventArgs e)
        {
            try
            {
                switch (e.MessageID)
                {
                    case "Page.frameAttached":
                        OnFrameAttached(e.MessageData.ToObject<PageFrameAttachedResponse>());
                        break;

                    case "Page.frameNavigated":
                        await OnFrameNavigatedAsync(e.MessageData.ToObject<PageFrameNavigatedResponse>(true).Frame).ConfigureAwait(false);
                        break;

                    case "Page.navigatedWithinDocument":
                        OnFrameNavigatedWithinDocument(e.MessageData.ToObject<NavigatedWithinDocumentResponse>(true));
                        break;

                    case "Page.frameDetached":
                        OnFrameDetached(e.MessageData.ToObject<BasicFrameResponse>(true));
                        break;

                    case "Page.frameStoppedLoading":
                        OnFrameStoppedLoading(e.MessageData.ToObject<BasicFrameResponse>(true));
                        break;

                    case "Runtime.executionContextCreated":
                        await OnExecutionContextCreatedAsync(e.MessageData.ToObject<RuntimeExecutionContextCreatedResponse>(true).Context);
                        break;

                    case "Runtime.executionContextDestroyed":
                        OnExecutionContextDestroyed(e.MessageData.ToObject<RuntimeExecutionContextDestroyedResponse>(true).ExecutionContextId);
                        break;
                    case "Runtime.executionContextsCleared":
                        OnExecutionContextsCleared();
                        break;
                    case "Page.lifecycleEvent":
                        OnLifeCycleEvent(e.MessageData.ToObject<LifecycleEventResponse>(true));
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                var message = $"Connection failed to process {e.MessageID}. {ex.Message}. {ex.StackTrace}";
                _logger.LogError(ex, message);
                Client.Close(message);
            }
        }

        private void OnFrameStoppedLoading(BasicFrameResponse e)
        {
            if (_frames.TryGetValue(e.FrameId, out var frame))
            {
                frame.OnLoadingStopped();
                LifecycleEvent?.Invoke(this, new FrameEventArgs(frame));
            }
        }

        private void OnLifeCycleEvent(LifecycleEventResponse e)
        {
            if (_frames.TryGetValue(e.FrameId, out var frame))
            {
                frame.OnLifecycleEvent(e.LoaderId, e.Name);
                LifecycleEvent?.Invoke(this, new FrameEventArgs(frame));
            }
        }

        private void OnExecutionContextsCleared()
        {
            while (_contextIdToContext.Count > 0)
            {
                var contextItem = _contextIdToContext.ElementAt(0);
                _contextIdToContext.Remove(contextItem.Key);

                if (contextItem.Value.World != null)
                {
                    contextItem.Value.World.SetContext(null);
                }
            }
        }

        private void OnExecutionContextDestroyed(int executionContextId)
        {
            _contextIdToContext.TryGetValue(executionContextId, out var context);

            if (context != null)
            {
                _contextIdToContext.Remove(executionContextId);

                if (context.World != null)
                {
                    context.World.SetContext(null);
                }
            }
        }

        private async Task OnExecutionContextCreatedAsync(ContextPayload contextPayload)
        {
            var frameId = contextPayload.AuxData?.FrameId;
            var frame = !string.IsNullOrEmpty(frameId) ? await GetFrameAsync(frameId).ConfigureAwait(false) : null;
            DOMWorld world = null;

            if (frame != null)
            {
                if (contextPayload.AuxData?.IsDefault == true)
                {
                    world = frame.MainWorld;
                }
                else if (contextPayload.Name == UtilityWorldName)
                {
                    world = frame.SecondaryWorld;
                }
            }
            if (contextPayload.AuxData?.Type == DOMWorldType.Isolated)
            {
                _isolatedWorlds.Add(contextPayload.Name);
            }
            var context = new ExecutionContext(Client, contextPayload, world);
            if (world != null)
            {
                world.SetContext(context);
            }
            _contextIdToContext[contextPayload.Id] = context;
        }

        private void OnFrameDetached(BasicFrameResponse e)
        {
            if (_frames.TryGetValue(e.FrameId, out var frame))
            {
                RemoveFramesRecursively(frame);
            }
        }

        private async Task OnFrameNavigatedAsync(FramePayload framePayload)
        {
            var isMainFrame = string.IsNullOrEmpty(framePayload.ParentId);
            var frame = isMainFrame ? MainFrame : await GetFrameAsync(framePayload.Id);

            Contract.Assert(isMainFrame || frame != null, "We either navigate top level or have old version of the navigated frame");

            // Detach all child frames first.
            if (frame != null)
            {
                while (frame.ChildFrames.Count > 0)
                {
                    RemoveFramesRecursively(frame.ChildFrames[0]);
                }
            }

            // Update or create main frame.
            if (isMainFrame)
            {
                if (frame != null)
                {
                    // Update frame id to retain frame identity on cross-process navigation.
                    if (frame.Id != null)
                    {
                        _frames.TryRemove(frame.Id, out _);
                    }
                    frame.Id = framePayload.Id;
                }
                else
                {
                    // Initial main frame navigation.
                    frame = new Frame(this, Client, null, framePayload.Id);
                }
                _asyncFrames.AddItem(framePayload.Id, frame);
                MainFrame = frame;
            }

            // Update frame payload.
            frame.Navigated(framePayload);

            FrameNavigated?.Invoke(this, new FrameEventArgs(frame));
        }

        internal Frame[] GetFrames() => _frames.Values.ToArray();

        private void OnFrameNavigatedWithinDocument(NavigatedWithinDocumentResponse e)
        {
            if (_frames.TryGetValue(e.FrameId, out var frame))
            {
                frame.NavigatedWithinDocument(e.Url);

                var eventArgs = new FrameEventArgs(frame);
                FrameNavigatedWithinDocument?.Invoke(this, eventArgs);
                FrameNavigated?.Invoke(this, eventArgs);
            }
        }

        private void RemoveFramesRecursively(Frame frame)
        {
            while (frame.ChildFrames.Count > 0)
            {
                RemoveFramesRecursively(frame.ChildFrames[0]);
            }
            frame.Detach();
            _frames.TryRemove(frame.Id, out _);
            FrameDetached?.Invoke(this, new FrameEventArgs(frame));
        }

        private void OnFrameAttached(PageFrameAttachedResponse frameAttached)
            => OnFrameAttached(frameAttached.FrameId, frameAttached.ParentFrameId);

        private void OnFrameAttached(string frameId, string parentFrameId)
        {
            if (!_frames.ContainsKey(frameId) && _frames.ContainsKey(parentFrameId))
            {
                var parentFrame = _frames[parentFrameId];
                var frame = new Frame(this, Client, parentFrame, frameId);
                _frames[frame.Id] = frame;
                FrameAttached?.Invoke(this, new FrameEventArgs(frame));
            }
        }

        private async Task HandleFrameTreeAsync(FrameTree frameTree)
        {
            if (!string.IsNullOrEmpty(frameTree.Frame.ParentId))
            {
                OnFrameAttached(frameTree.Frame.Id, frameTree.Frame.ParentId);
            }

            await OnFrameNavigatedAsync(frameTree.Frame);

            if (frameTree.Childs != null)
            {
                foreach (var child in frameTree.Childs)
                {
                    await HandleFrameTreeAsync(child);
                }
            }
        }

        internal Task EnsureSecondaryDOMWorldAsync() => EnsureSecondaryDOMWorldAsync(UtilityWorldName);

        internal async Task EnsureSecondaryDOMWorldAsync(string name)
        {
            if (_isolatedWorlds.Contains(name))
            {
                return;
            }
            _isolatedWorlds.Add(name);
            await Client.SendAsync("Page.addScriptToEvaluateOnNewDocument", new PageAddScriptToEvaluateOnNewDocumentRequest
            {
                Source = $"//# sourceURL={ExecutionContext.EvaluationScriptUrl}",
                WorldName = name,
            });
            await Task.WhenAll(GetFrames().Select(frame => Client.SendAsync("Page.createIsolatedWorld", new PageCreateIsolatedWorldRequest
            {
                FrameId = frame.Id,
                GrantUniveralAccess = true,
                WorldName = name
            }))).ConfigureAwait(false);
        }

        internal Task<Frame> GetFrameAsync(string frameId) => _asyncFrames.GetItemAsync(frameId);

        #endregion
    }
}
