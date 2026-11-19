using System;
using System.Collections.Generic;
using System.Linq;
using Blish_HUD.Controls;

namespace entrhopi.HomeTab.Contracts
{
    /// <summary>
    /// A block that can be assigned to one of the Home tab quadrants.
    /// </summary>
    public interface IHomeTabBlock
    {
        /// <summary>
        /// Stable, unique identifier for this block. Prefix it with the providing
        /// module's namespace to avoid collisions with other providers.
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Name shown in the quadrant header and in the Home Tab settings dropdown.
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Creates the block content. The Home tab owns the quadrant header and
        /// gives the returned control the full content area.
        /// </summary>
        Control CreateContent();
    }

    /// <summary>
    /// Supplies one or more Home tab blocks. Register this once when the
    /// providing module loads and unregister it when the module unloads.
    /// </summary>
    public interface IHomeTabBlockProvider
    {
        /// <summary>
        /// Stable identifier for the provider, normally the module namespace.
        /// </summary>
        string ProviderId { get; }

        /// <summary>
        /// Returns the blocks currently supplied by this provider.
        /// </summary>
        IEnumerable<IHomeTabBlock> GetBlocks();
    }

    /// <summary>
    /// Shared registry used by Home Tab to discover blocks from other modules.
    /// </summary>
    public static class HomeTabBlockRegistry
    {
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, IHomeTabBlockProvider> Providers =
            new Dictionary<string, IHomeTabBlockProvider>(StringComparer.OrdinalIgnoreCase);

        public static event EventHandler BlocksChanged;

        public static void Register(IHomeTabBlockProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            if (string.IsNullOrWhiteSpace(provider.ProviderId))
                throw new ArgumentException("A provider ID is required.", nameof(provider));

            lock (SyncRoot)
            {
                Providers[provider.ProviderId] = provider;
            }

            BlocksChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void Unregister(IHomeTabBlockProvider provider)
        {
            if (provider == null) return;

            bool removed;

            lock (SyncRoot)
            {
                removed = Providers.Remove(provider.ProviderId);
            }

            if (removed)
            {
                BlocksChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static IReadOnlyList<IHomeTabBlock> GetBlocks()
        {
            lock (SyncRoot)
            {
                return Providers.Values
                                .SelectMany(GetProviderBlocks)
                                .Where(block => block != null
                                             && !string.IsNullOrWhiteSpace(block.Id)
                                             && !string.IsNullOrWhiteSpace(block.DisplayName))
                                .GroupBy(block => block.Id, StringComparer.OrdinalIgnoreCase)
                                .Select(group => group.First())
                                .OrderBy(block => block.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                                .ToList();
            }
        }

        private static IEnumerable<IHomeTabBlock> GetProviderBlocks(IHomeTabBlockProvider provider)
        {
            try
            {
                return provider.GetBlocks() ?? Enumerable.Empty<IHomeTabBlock>();
            }
            catch
            {
                return Enumerable.Empty<IHomeTabBlock>();
            }
        }
    }
}
