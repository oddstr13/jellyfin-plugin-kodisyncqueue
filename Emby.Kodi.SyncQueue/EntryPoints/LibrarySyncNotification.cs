﻿using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MoreLinq;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Emby.Kodi.SyncQueue.Helpers;
using System.Threading.Tasks;

namespace Emby.Kodi.SyncQueue.EntryPoints
{
    public class LibrarySyncNotification : IServerEntryPoint
    {
        /// <summary>
        /// The _library manager
        /// </summary>
        private readonly ILibraryManager _libraryManager;

        private readonly ISessionManager _sessionManager;
        private readonly IUserManager _userManager;
        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IApplicationPaths _applicationPaths;

        /// <summary>
        /// The _library changed sync lock
        /// </summary>
        private readonly object _libraryChangedSyncLock = new object();

        private readonly List<Folder> _foldersAddedTo = new List<Folder>();
        private readonly List<Folder> _foldersRemovedFrom = new List<Folder>();

        private readonly List<BaseItem> _itemsAdded = new List<BaseItem>();
        private readonly List<BaseItem> _itemsRemoved = new List<BaseItem>();
        private readonly List<BaseItem> _itemsUpdated = new List<BaseItem>();

        private DataHelper dataHelper;

        /// <summary>
        /// Gets or sets the library update timer.
        /// </summary>
        /// <value>The library update timer.</value>
        private Timer LibraryUpdateTimer { get; set; }

        /// <summary>
        /// The library update duration
        /// </summary>
        private const int LibraryUpdateDuration = 5000;

        public LibrarySyncNotification(ILibraryManager libraryManager, ISessionManager sessionManager, IUserManager userManager, ILogger logger, IJsonSerializer jsonSerializer, IApplicationPaths applicationPaths)
        {
            _libraryManager = libraryManager;
            _sessionManager = sessionManager;
            _userManager = userManager;
            _logger = logger;
            _jsonSerializer = jsonSerializer;
            _applicationPaths = applicationPaths;
        }

        public void Run()
        {
            _libraryManager.ItemAdded += libraryManager_ItemAdded;
            _libraryManager.ItemUpdated += libraryManager_ItemUpdated;
            _libraryManager.ItemRemoved += libraryManager_ItemRemoved;

            _logger.Info("Emby.Kodi.Sync.Queue:  LibrarySyncNotification Startup...");
            dataHelper = new DataHelper(_logger, _jsonSerializer);
            string dataPath = _applicationPaths.DataPath;
            bool result;

            result = dataHelper.CheckCreateFiles(dataPath);

            if (result)
                result = dataHelper.OpenConnection();
            if (result)
                result = dataHelper.CreateLibraryTable("ItemsAddedQueue", "IAQUnique");
            if (result)
                result = dataHelper.CreateLibraryTable("ItemsUpdatedQueue", "IUQUnique");
            if (result)
                result = dataHelper.CreateLibraryTable("ItemsRemovedQueue", "IRQUnique");
            if (result)
                result = dataHelper.CreateLibraryTable("FoldersAddedQueue", "FAQUnique");
            if (result)
                result = dataHelper.CreateLibraryTable("FoldersRemovedQueue", "FRQUnique");

            if (!result)
            {
                throw new ApplicationException("Emby.Kodi.SyncQueue:  Could Not Be Loaded Due To Previous Error!");
            }
        }

        /// <summary>
        /// Handles the ItemAdded event of the libraryManager control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="ItemChangeEventArgs"/> instance containing the event data.</param>
        void libraryManager_ItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (!FilterItem(e.Item))
            {
                return;
            }

            lock (_libraryChangedSyncLock)
            {
                if (LibraryUpdateTimer == null)
                {
                    LibraryUpdateTimer = new Timer(LibraryUpdateTimerCallback, null, LibraryUpdateDuration,
                                                   Timeout.Infinite);
                }
                else
                {
                    LibraryUpdateTimer.Change(LibraryUpdateDuration, Timeout.Infinite);
                }

                if (e.Item.Parent != null)
                {
                    _foldersAddedTo.Add(e.Item.Parent);
                }

                _itemsAdded.Add(e.Item);
            }
        }

        /// <summary>
        /// Handles the ItemUpdated event of the libraryManager control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="ItemChangeEventArgs"/> instance containing the event data.</param>
        void libraryManager_ItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (!FilterItem(e.Item))
            {
                return;
            }

            lock (_libraryChangedSyncLock)
            {
                if (LibraryUpdateTimer == null)
                {
                    LibraryUpdateTimer = new Timer(LibraryUpdateTimerCallback, null, LibraryUpdateDuration,
                                                   Timeout.Infinite);
                }
                else
                {
                    LibraryUpdateTimer.Change(LibraryUpdateDuration, Timeout.Infinite);
                }

                _itemsUpdated.Add(e.Item);
            }
        }

        /// <summary>
        /// Handles the ItemRemoved event of the libraryManager control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="ItemChangeEventArgs"/> instance containing the event data.</param>
        void libraryManager_ItemRemoved(object sender, ItemChangeEventArgs e)
        {
            if (!FilterItem(e.Item))
            {
                return;
            }

            lock (_libraryChangedSyncLock)
            {
                if (LibraryUpdateTimer == null)
                {
                    LibraryUpdateTimer = new Timer(LibraryUpdateTimerCallback, null, LibraryUpdateDuration,
                                                   Timeout.Infinite);
                }
                else
                {
                    LibraryUpdateTimer.Change(LibraryUpdateDuration, Timeout.Infinite);
                }

                if (e.Item.Parent != null)
                {
                    _foldersRemovedFrom.Add(e.Item.Parent);
                }

                _itemsRemoved.Add(e.Item);
            }
        }

        /// <summary>
        /// Libraries the update timer callback.
        /// </summary>
        /// <param name="state">The state.</param>
        private void LibraryUpdateTimerCallback(object state)
        {
            lock (_libraryChangedSyncLock)
            {
                // Remove dupes in case some were saved multiple times
                var foldersAddedTo = _foldersAddedTo.DistinctBy(i => i.Id).ToList();

                var foldersRemovedFrom = _foldersRemovedFrom.DistinctBy(i => i.Id).ToList();

                var itemsUpdated = _itemsUpdated
                    .Where(i => !_itemsAdded.Contains(i))
                    .DistinctBy(i => i.Id)
                    .ToList();

                SendChangeNotifications(_itemsAdded.ToList(), itemsUpdated, _itemsRemoved.ToList(), foldersAddedTo, foldersRemovedFrom, CancellationToken.None);

                if (LibraryUpdateTimer != null)
                {
                    LibraryUpdateTimer.Dispose();
                    LibraryUpdateTimer = null;
                }

                _itemsAdded.Clear();
                _itemsRemoved.Clear();
                _itemsUpdated.Clear();
                _foldersAddedTo.Clear();
                _foldersRemovedFrom.Clear();
            }
        }

        /// <summary>
        /// Sends the change notifications.
        /// </summary>
        /// <param name="itemsAdded">The items added.</param>
        /// <param name="itemsUpdated">The items updated.</param>
        /// <param name="itemsRemoved">The items removed.</param>
        /// <param name="foldersAddedTo">The folders added to.</param>
        /// <param name="foldersRemovedFrom">The folders removed from.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async void SendChangeNotifications(List<BaseItem> itemsAdded, List<BaseItem> itemsUpdated, List<BaseItem> itemsRemoved, List<Folder> foldersAddedTo, List<Folder> foldersRemovedFrom, CancellationToken cancellationToken)
        {
            foreach (var user in _userManager.Users.ToList())
            {
                var id = user.Id;
                var userName = user.Name;
                var userSessions = _sessionManager.Sessions
                    .Where(u => u.UserId.HasValue && u.UserId.Value == id && u.IsActive)
                    .ToList();

                var info = GetLibraryUpdateInfo(itemsAdded, itemsUpdated, itemsRemoved, foldersAddedTo,
                                                foldersRemovedFrom, id);
                int[] iTasks = new int[5];

                // I am doing this to strip out information that doesn't usually make it to the websocket...
                // Will query Luke about that at a later time...
                var json = _jsonSerializer.SerializeToString(info); //message
                var dejson = _jsonSerializer.DeserializeFromString<LibraryUpdateInfo>(json);

                _logger.Debug(String.Format("Emby.Kodi.SyncQueue:  User: {0} - {1}", userName, id));
                _logger.Debug(String.Format("Emby.Kodi.SyncQueue:  Items Added:          {0}", dejson.ItemsAdded.Count.ToString()));
                _logger.Debug(String.Format("Emby.Kodi.SyncQueue:  Items Updated:        {0}", dejson.ItemsUpdated.Count.ToString()));
                _logger.Debug(String.Format("Emby.Kodi.SyncQueue:  Items Removed:        {0}", dejson.ItemsRemoved.Count.ToString()));
                _logger.Debug(String.Format("Emby.Kodi.SyncQueue:  Folders Added To:     {0}", dejson.FoldersAddedTo.Count.ToString()));
                _logger.Debug(String.Format("Emby.Kodi.SyncQueue:  Folders Removed From: {0}", dejson.FoldersRemovedFrom.Count.ToString()));

                Task<int> iaTask = dataHelper.LibraryItemsToAlter(dejson.ItemsAdded, id.ToString("N"), "ItemsAddedQueue", cancellationToken);
                Task<int> iuTask = dataHelper.LibraryItemsToAlter(dejson.ItemsUpdated, id.ToString("N"), "ItemsUpdatedQueue", cancellationToken);
                Task<int> irTask = dataHelper.LibraryItemsToAlter(dejson.ItemsRemoved, id.ToString("N"), "ItemsRemovedQueue", cancellationToken);
                Task<int> faTask = dataHelper.LibraryItemsToAlter(dejson.FoldersAddedTo, id.ToString("N"), "FoldersAddedQueue", cancellationToken);
                Task<int> frTask = dataHelper.LibraryItemsToAlter(dejson.FoldersRemovedFrom, id.ToString("N"), "FoldersRemovedQueue", cancellationToken);

                iTasks[0] = await iaTask;
                iTasks[1] = await iuTask;
                iTasks[2] = await irTask;
                iTasks[3] = await faTask;
                iTasks[4] = await frTask;
            }
        }



        /// <summary>
        /// Gets the library update info.
        /// </summary>
        /// <param name="itemsAdded">The items added.</param>
        /// <param name="itemsUpdated">The items updated.</param>
        /// <param name="itemsRemoved">The items removed.</param>
        /// <param name="foldersAddedTo">The folders added to.</param>
        /// <param name="foldersRemovedFrom">The folders removed from.</param>
        /// <param name="userId">The user id.</param>
        /// <returns>LibraryUpdateInfo.</returns>
        private LibraryUpdateInfo GetLibraryUpdateInfo(IEnumerable<BaseItem> itemsAdded, IEnumerable<BaseItem> itemsUpdated, IEnumerable<BaseItem> itemsRemoved, IEnumerable<Folder> foldersAddedTo, IEnumerable<Folder> foldersRemovedFrom, Guid userId)
        {
            var user = _userManager.GetUserById(userId);

            return new LibraryUpdateInfo
            {
                ItemsAdded = itemsAdded.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user)).Select(i => i.Id.ToString("N")).Distinct().ToList(),

                ItemsUpdated = itemsUpdated.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user)).Select(i => i.Id.ToString("N")).Distinct().ToList(),

                ItemsRemoved = itemsRemoved.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user, true)).Select(i => i.Id.ToString("N")).Distinct().ToList(),

                FoldersAddedTo = foldersAddedTo.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user)).Select(i => i.Id.ToString("N")).Distinct().ToList(),

                FoldersRemovedFrom = foldersRemovedFrom.SelectMany(i => TranslatePhysicalItemToUserLibrary(i, user)).Select(i => i.Id.ToString("N")).Distinct().ToList()
            };
        }

        private bool FilterItem(BaseItem item)
        {
            if (item.LocationType == LocationType.Virtual)
            {
                return false;
            }

            return !(item is IChannelItem);
        }

        /// <summary>
        /// Translates the physical item to user library.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="item">The item.</param>
        /// <param name="user">The user.</param>
        /// <param name="includeIfNotFound">if set to <c>true</c> [include if not found].</param>
        /// <returns>IEnumerable{``0}.</returns>
        private IEnumerable<T> TranslatePhysicalItemToUserLibrary<T>(T item, User user, bool includeIfNotFound = false)
            where T : BaseItem
        {
            // If the physical root changed, return the user root
            if (item is AggregateFolder)
            {
                return new[] { user.RootFolder as T };
            }

            // Return it only if it's in the user's library
            if (includeIfNotFound || item.IsVisibleStandalone(user))
            {
                return new[] { item };
            }

            return new T[] { };
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                if (LibraryUpdateTimer != null)
                {
                    LibraryUpdateTimer.Dispose();
                    LibraryUpdateTimer = null;
                }

                _libraryManager.ItemAdded -= libraryManager_ItemAdded;
                _libraryManager.ItemUpdated -= libraryManager_ItemUpdated;
                _libraryManager.ItemRemoved -= libraryManager_ItemRemoved;
            }
        }
    }
}