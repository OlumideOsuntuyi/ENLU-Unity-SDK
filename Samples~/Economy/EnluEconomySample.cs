using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enlu;
using UnityEngine;

namespace Enlu.Samples
{
    /// <summary>
    /// ENLU economy sample: wallet balances, a shop built from the catalog, buying with virtual
    /// currency (with friendly error toasts), an inventory with consume, and achievement progress.
    /// Add it to a GameObject, assign settings (or keep Resources/EnluSettings) and press Play.
    /// Create a currency and a few items first (Tools > ENLU > Account > Economy, or the dashboard).
    /// </summary>
    public sealed class EnluEconomySample : MonoBehaviour
    {
        [Tooltip("Leave empty to use Resources/EnluSettings.")]
        [SerializeField] private EnluSettings settings;

        [Tooltip("Optional store id: shows that store's items and sale prices instead of the whole catalog.")]
        [SerializeField] private string storeId = "";

        private EnluClient _client;
        private List<Currency> _currencies = new List<Currency>();
        private List<CatalogItem> _catalog = new List<CatalogItem>();
        private Inventory _inventory;
        private List<AchievementProgress> _achievements = new List<AchievementProgress>();
        private bool _busy;
        private string _toast;
        private float _toastUntil;
        private Vector2 _scroll;

        private async void Start()
        {
            try
            {
                _client = settings != null ? new EnluClient(settings) : EnluClient.Default;
                if (!_client.IsLoggedIn) await _client.LoginWithDeviceIdAsync();
                await RefreshAll();
            }
            catch (Exception e)
            {
                Toast("Could not start: " + e.Message);
            }
        }

        private async Task RefreshAll()
        {
            _busy = true;
            try
            {
                _currencies = await _client.GetCurrenciesAsync();
                _catalog = await _client.GetCatalogAsync(string.IsNullOrEmpty(storeId) ? null : storeId);
                _inventory = await _client.GetInventoryAsync();
                _achievements = await _client.GetAchievementsAsync();
            }
            catch (EnluException e)
            {
                Toast(e.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private async void Buy(CatalogItem item, string currency, long price)
        {
            _busy = true;
            try
            {
                var result = await _client.PurchaseItemAsync(item.ItemId, currency, price, string.IsNullOrEmpty(storeId) ? null : storeId);
                Toast("Bought " + item.Name + "! " + currency + " left: " + (result.Currencies.TryGetValue(currency, out var left) ? left : 0));
                _inventory = await _client.GetInventoryAsync();
                _achievements = await _client.GetAchievementsAsync(); // purchases can move achievements
            }
            catch (EnluException e) when (e.IsInsufficientFunds)
            {
                Toast("Not enough " + currency + " for " + item.Name + ".");
            }
            catch (EnluException e) when (e.IsPriceChanged)
            {
                Toast("The price of " + item.Name + " changed to " + e.NewPrice + " " + currency + ". Check the shop again.");
                _catalog = await _client.GetCatalogAsync(string.IsNullOrEmpty(storeId) ? null : storeId);
            }
            catch (EnluException e) when (e.IsLimitReached)
            {
                Toast("You already have as many " + item.Name + " as allowed.");
            }
            catch (EnluException e) when (e.IsNetworkError)
            {
                // Purchases are never retried automatically: check whether it went through before buying again.
                Toast("Connection problem. Checking your inventory before you try again…");
                try
                {
                    _inventory = await _client.GetInventoryAsync();
                }
                catch (EnluException)
                {
                }
            }
            catch (EnluException e)
            {
                Toast("Purchase failed: " + e.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private async void Consume(ItemInstance instance)
        {
            _busy = true;
            try
            {
                var r = await _client.ConsumeItemAsync(instance.InstanceId);
                Toast("Used " + instance.ItemId + (r.Instance == null ? " (none left)" : ""));
                _inventory = await _client.GetInventoryAsync();
                _achievements = await _client.GetAchievementsAsync();
            }
            catch (EnluException e) when (e.HttpStatus == 403)
            {
                Toast(instance.ItemId + " cannot be used from the game.");
            }
            catch (EnluException e)
            {
                Toast("Could not use it: " + e.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private void Toast(string message)
        {
            _toast = message;
            _toastUntil = Time.realtimeSinceStartup + 4f;
            Debug.Log("[ENLU economy sample] " + message);
        }

        private string CurrencyName(string code) => _currencies.FirstOrDefault(c => c.Code == code)?.Name ?? code;

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, Mathf.Min(Screen.width - 20, 640), Screen.height - 20), GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("Wallet");
            if (_inventory != null)
                foreach (var c in _currencies)
                    GUILayout.Label("  " + c.Name + ": " + _inventory.GetBalance(c.Code) + (c.RechargeRate.HasValue ? "  (+" + c.RechargeRate + "/h)" : ""));

            GUILayout.Space(8);
            GUILayout.Label("Shop" + (string.IsNullOrEmpty(storeId) ? "" : " (" + storeId + ")"));
            GUI.enabled = !_busy;
            foreach (var item in _catalog.Where(i => i.Enabled && i.Prices.Count > 0))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("  " + item.Name + (item.Bundle != null ? " (bundle)" : ""), GUILayout.Width(240));
                foreach (var price in item.Prices)
                    if (GUILayout.Button(price.Value + " " + CurrencyName(price.Key), GUILayout.Width(140)))
                        Buy(item, price.Key, price.Value);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8);
            GUILayout.Label("Inventory");
            if (_inventory != null)
            {
                if (_inventory.Items.Count == 0) GUILayout.Label("  (empty)");
                foreach (var inst in _inventory.Items)
                {
                    var item = _catalog.FirstOrDefault(i => i.ItemId == inst.ItemId);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("  " + (item?.Name ?? inst.ItemId) + " x" + inst.Count +
                                    (inst.UsesLeft.HasValue ? ", " + inst.UsesLeft + " uses" : "") +
                                    (inst.ExpiresAt.HasValue ? ", expires " + inst.ExpiresAt.Value.ToLocalTime().ToString("g") : ""), GUILayout.Width(380));
                    if (item != null && item.Consumable && GUILayout.Button("Use", GUILayout.Width(60))) Consume(inst);
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Space(8);
            GUILayout.Label("Achievements");
            foreach (var a in _achievements)
                GUILayout.Label("  " + (a.IsUnlocked ? "[x] " : "[ ] ") + a.Name + "  " + Mathf.RoundToInt((float)a.Progress * 100) + "%" +
                                (string.IsNullOrEmpty(a.Description) ? "" : "  - " + a.Description));

            GUILayout.Space(8);
            if (GUILayout.Button("Refresh")) _ = RefreshAll();
            GUI.enabled = true;
            GUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_toast) && Time.realtimeSinceStartup < _toastUntil)
                GUILayout.Box(_toast);
            GUILayout.EndArea();
        }
    }
}
