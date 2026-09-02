using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

public class CPHInline
{
    private const string InventoryActionName = "[IMS] Core";
    private const string CurrencyActionName = "[CMS] Core";
    private const string RootPath = @"data\ShopManagement";
    private const int SchemaVersion = 1;
    private const bool DefaultSendMessage = true;
    private static readonly object Sync = new object ();
    private Request request;
    private Response response;
    private enum NumberState
    {
        Missing,
        Valid,
        Invalid
    }

    public bool Execute()
    {
        Reset();
        try
        {
            if (!Read())
                return true;
            lock (Sync)
            {
                Database db;
                if (!Load(out db))
                    return true;
                Process(db);
            }
        }
        catch (Exception ex)
        {
            Fail("Failed", "SHOP-9000", "An unexpected shop error occurred.", ex.ToString());
        }

        return true;
    }

    private bool Read()
    {
        request = new Request
        {
            Operation = Text("shopInputOperation").Trim().ToLowerInvariant(),
            ShopName = Text("shopInputShopName").Trim(),
            Platform = Text("shopInputPlatform").Trim(),
            OwnerId = Text("shopInputOwnerId").Trim(),
            UserName = Text("shopInputUserName").Trim(),
            PointId = Text("shopInputPointId").Trim(),
            InventoryName = Text("shopInputInventoryName").Trim(),
            ItemId = Text("shopInputItemId").Trim(),
            ItemName = Text("shopInputItemName").Trim(),
            SendMessage = NullableBool("shopInputSendResultMessage")
        };
        if (request.InventoryName.Length == 0)
            request.InventoryName = "default";
        string[] ops =
        {
            "registershopitem",
            "updateshopitem",
            "getshopitem",
            "removeshopitem",
            "enableshopitem",
            "disableshopitem",
            "canbuy",
            "buy",
            "cansell",
            "sell"
        };
        if (!ops.Contains(request.Operation))
            return Fail("ValidationFailed", "SHOP-1002", "The shop operation is unsupported.", request.Operation);
        if (!ValidName(request.ShopName))
            return Fail("ValidationFailed", "SHOP-1101", "A valid shop name is required.", request.ShopName);
        bool trade = request.Operation == "canbuy" || request.Operation == "buy" || request.Operation == "cansell" || request.Operation == "sell";
        if (trade && (request.Platform.Length == 0 || request.OwnerId.Length == 0 || request.UserName.Length == 0 || request.PointId.Length == 0))
            return Fail("ValidationFailed", "SHOP-1102", "Platform, owner ID, username, and point ID are required for trades.", "");
        NumberState n = Number("shopInputQuantity", out request.Quantity);
        if (n == NumberState.Invalid)
            return false;
        if (n == NumberState.Missing)
            request.Quantity = 1;
        NumberState b = Number("shopInputBuyPrice", out request.BuyPrice);
        if (b == NumberState.Invalid)
            return false;
        request.HasBuyPrice = b == NumberState.Valid;
        NumberState s = Number("shopInputSellPrice", out request.SellPrice);
        if (s == NumberState.Invalid)
            return false;
        request.HasSellPrice = s == NumberState.Valid;
        NumberState m = Number("shopInputMaximumPerTransaction", out request.Maximum);
        if (m == NumberState.Invalid)
            return false;
        request.HasMaximum = m == NumberState.Valid;
        request.Enabled = NullableBool("shopInputEnabled");
        return true;
    }

    private void Process(Database db)
    {
        if (request.Operation == "registershopitem")
        {
            Register(db);
            return;
        }

        Listing item;
        if (!Resolve(db, out item))
            return;
        SetItem(item);
        if (request.Operation == "getshopitem")
        {
            Success("ShopItemRetrieved", "The shop item was retrieved.");
            return;
        }

        if (request.Operation == "removeshopitem")
        {
            db.Items.Remove(item.ItemId);
            if (Save(db))
                Success("ShopItemRemoved", "The item was removed from the shop.");
            return;
        }

        if (request.Operation == "enableshopitem" || request.Operation == "disableshopitem")
        {
            item.Enabled = request.Operation == "enableshopitem";
            if (Save(db))
                Success(item.Enabled ? "ShopItemEnabled" : "ShopItemDisabled", item.Enabled ? "The shop item was enabled." : "The shop item was disabled.");
            return;
        }

        if (request.Operation == "updateshopitem")
        {
            bool c = false;
            if (request.HasBuyPrice)
            {
                item.BuyPrice = request.BuyPrice;
                c = true;
            }

            if (request.HasSellPrice)
            {
                item.SellPrice = request.SellPrice;
                c = true;
            }

            if (request.HasMaximum)
            {
                item.Maximum = request.Maximum;
                c = true;
            }

            if (request.Enabled.HasValue)
            {
                item.Enabled = request.Enabled.Value;
                c = true;
            }

            if (c && !Save(db))
                return;
            SetItem(item);
            Success("ShopItemUpdated", c ? "The shop item was updated." : "No shop item values changed.");
            return;
        }

        if (request.Operation == "canbuy" || request.Operation == "buy")
            Trade(item, true, request.Operation == "buy");
        else
            Trade(item, false, request.Operation == "sell");
    }

    private void Register(Database db)
    {
        if (request.ItemId.Length == 0)
        {
            Fail("ValidationFailed", "SHOP-1201", "RegisterShopItem requires an item ID.", "");
            return;
        }

        if (db.Items.ContainsKey(Norm(request.ItemId)))
        {
            Fail("ShopItemExists", "SHOP-1202", "The item is already in this shop.", request.ItemId);
            return;
        }

        InventoryResponse ir;
        if (!Inventory("GetItem", 0, out ir))
            return;
        Listing l = new Listing
        {
            ItemId = ir.ItemId,
            DisplayName = ir.ItemName,
            BuyPrice = request.BuyPrice,
            SellPrice = request.SellPrice,
            Maximum = request.Maximum,
            Enabled = request.Enabled ?? true
        };
        db.Items[l.ItemId] = l;
        if (!Save(db))
            return;
        SetItem(l);
        Success("ShopItemRegistered", l.DisplayName + " was registered in shop " + request.ShopName + ".");
    }

    private void Trade(Listing item, bool buying, bool mutate)
    {
        if (!item.Enabled)
        {
            Fail("ItemDisabled", "SHOP-2201", "This shop item is disabled.", item.ItemId);
            return;
        }

        if (request.Quantity <= 0 || item.Maximum > 0 && request.Quantity > item.Maximum)
        {
            Fail("LimitExceeded", "SHOP-2202", "The requested quantity is invalid or exceeds the transaction limit.", "");
            return;
        }

        long price = buying ? item.BuyPrice : item.SellPrice;
        if (request.Quantity > 0 && price > long.MaxValue / request.Quantity)
        {
            Fail("PriceOverflow", "SHOP-1401", "The total price would overflow.", "");
            return;
        }

        long total = price * request.Quantity;
        response.Quantity = request.Quantity;
        response.TotalPrice = total;
        InventoryResponse ir;
        CurrencyResponse cr;
        if (buying)
        {
            if (!Inventory("CanAdd", request.Quantity, out ir))
                return;
            if (!ir.CanAdd)
            {
                response.CanBuy = false;
                Success("CannotBuy", "The item cannot be added to the inventory.");
                return;
            }

            if (!Currency("CanRemove", total, out cr))
                return;
            if (!cr.CanRemove)
            {
                response.CanBuy = false;
                Success("CannotBuy", "The user has insufficient currency.");
                return;
            }

            response.CanBuy = true;
            if (!mutate)
            {
                Success("CanBuy", "The user can buy the requested quantity.");
                return;
            }

            if (!Currency("Remove", total, out cr))
                return;
            Copy(cr);
            if (!Inventory("Add", request.Quantity, out ir))
            {
                response.RollbackAttempted = true;
                CurrencyResponse refund;
                response.RollbackSuccessful = Currency("Add", total, out refund);
                Fail("BuyFailed", response.RollbackSuccessful ? "SHOP-2701" : "SHOP-2702", response.RollbackSuccessful ? "The purchase failed and the charge was refunded." : "The purchase and refund both failed.", "");
                return;
            }

            response.InventoryChanged = true;
            response.PreviousQuantity = ir.PreviousQuantity;
            response.NewQuantity = ir.NewQuantity;
            Success("Bought", "The purchase completed successfully.");
        }
        else
        {
            if (!Inventory("CanRemove", request.Quantity, out ir))
                return;
            if (!ir.CanRemove)
            {
                response.CanSell = false;
                Success("CannotSell", "The user does not own the required quantity.");
                return;
            }

            if (!Currency("CanAdd", total, out cr))
                return;
            if (!cr.CanAdd)
            {
                response.CanSell = false;
                Success("CannotSell", "The sale proceeds would overflow the balance.");
                return;
            }

            response.CanSell = true;
            if (!mutate)
            {
                Success("CanSell", "The user can sell the requested quantity.");
                return;
            }

            if (!Inventory("Remove", request.Quantity, out ir))
                return;
            response.InventoryChanged = true;
            if (!Currency("Add", total, out cr))
            {
                response.RollbackAttempted = true;
                InventoryResponse restore;
                response.RollbackSuccessful = Inventory("Add", request.Quantity, out restore);
                Fail("SellFailed", response.RollbackSuccessful ? "SHOP-2703" : "SHOP-2704", response.RollbackSuccessful ? "The sale failed and the item was restored." : "The sale and rollback both failed.", "");
                return;
            }

            Copy(cr);
            response.PreviousQuantity = ir.PreviousQuantity;
            response.NewQuantity = ir.NewQuantity;
            Success("Sold", "The sale completed successfully.");
        }
    }

    private bool Inventory(string operation, long quantity, out InventoryResponse result)
    {
        result = null;
        string requestId = Guid.NewGuid().ToString("N");
        string requestKey = "inventory.request." + requestId;
        string responseKey = "inventory.response." + requestId;
        string currentKey = "inventory.request.current";
        Dictionary<string, object> envelope = new Dictionary<string, object>
        {
            {
                "RequestId",
                requestId
            },
            {
                "Operation",
                operation
            },
            {
                "Platform",
                request.Platform
            },
            {
                "OwnerId",
                request.OwnerId
            },
            {
                "InventoryName",
                request.InventoryName
            },
            {
                "ItemId",
                request.ItemId
            },
            {
                "ItemName",
                string.Empty
            },
            {
                "Quantity",
                quantity
            },
            {
                "Metadata",
                string.Empty
            },
            {
                "SendMessage",
                false
            }
        };
        CPH.SetGlobalVar(requestKey, JsonConvert.SerializeObject(envelope), false);
        CPH.SetGlobalVar(currentKey, requestId, false);
        string verifiedCurrentRequestId = CPH.GetGlobalVar<string>(currentKey, false);
        string verifiedRequestJson = CPH.GetGlobalVar<string>(requestKey, false);
        CPH.LogInfo("[Shop API] Current ID written='" + requestId + "', read='" + (verifiedCurrentRequestId ?? "<null>") + "'.");
        CPH.LogInfo("[Shop API] Request JSON verification: " + (string.IsNullOrWhiteSpace(verifiedRequestJson) ? "<missing>" : verifiedRequestJson));
        try
        {
            if (!CPH.RunAction(InventoryActionName, true))
                return Fail("InventoryCallFailed", "SHOP-2501", "The Inventory action could not be run.", InventoryActionName);
            string responseJson = CPH.GetGlobalVar<string>(responseKey, false);
            string json = CPH.GetGlobalVar<string>(responseKey, false);
            if (string.IsNullOrWhiteSpace(json))
                return Fail("InventoryResponseMissing", "SHOP-2503", "The Inventory action did not return a response.", responseKey);
            try
            {
                result = JsonConvert.DeserializeObject<InventoryResponse>(json);
            }
            catch (Exception ex)
            {
                return Fail("InventoryResponseInvalid", "SHOP-2504", "The Inventory response was invalid.", ex.Message);
            }

            if (result == null || !result.Success)
                return Fail("InventoryRejected", "SHOP-2502", result == null ? "The Inventory response was empty." : result.ResultMessage + (string.IsNullOrWhiteSpace(result.StatusCode) ? "" : " (" + result.StatusCode + ")"), result == null ? "" : result.ErrorDetails);
            response.ItemId = result.ItemId;
            response.ItemName = result.ItemName;
            return true;
        }
        finally
        {
            CPH.UnsetGlobalVar(requestKey, false);
            CPH.UnsetGlobalVar(responseKey, false);
            string active = CPH.GetGlobalVar<string>(currentKey, false);
            if (string.Equals(active, requestId, StringComparison.Ordinal))
                CPH.UnsetGlobalVar(currentKey, false);
        }
    }

    private bool Currency(string operation, long amount, out CurrencyResponse result)
    {
        result = null;
        string requestId = Guid.NewGuid().ToString("N");
        string requestKey = "currency.request." + requestId;
        string responseKey = "currency.response." + requestId;
        string currentKey = "currency.request.current";
        Dictionary<string, object> envelope = new Dictionary<string, object>
        {
            {
                "RequestId",
                requestId
            },
            {
                "Operation",
                operation
            },
            {
                "Platform",
                request.Platform
            },
            {
                "UserId",
                request.OwnerId
            },
            {
                "UserName",
                request.UserName
            },
            {
                "PointId",
                request.PointId
            },
            {
                "Destination",
                string.Empty
            },
            {
                "Amount",
                amount
            },
            {
                "UseMultipliers",
                false
            },
            {
                "SendMessage",
                false
            }
        };
        CPH.SetGlobalVar(requestKey, JsonConvert.SerializeObject(envelope), false);
        CPH.SetGlobalVar(currentKey, requestId, false);
        try
        {
            if (!CPH.RunAction(CurrencyActionName, true))
                return Fail("CurrencyCallFailed", "SHOP-2601", "The Currency action could not be run.", CurrencyActionName);
            string json = CPH.GetGlobalVar<string>(responseKey, false);
            if (string.IsNullOrWhiteSpace(json))
                return Fail("CurrencyResponseMissing", "SHOP-2603", "The Currency action did not return a response.", responseKey);
            try
            {
                result = JsonConvert.DeserializeObject<CurrencyResponse>(json);
            }
            catch (Exception ex)
            {
                return Fail("CurrencyResponseInvalid", "SHOP-2604", "The Currency response was invalid.", ex.Message);
            }

            if (result == null || !result.Success)
                return Fail("CurrencyRejected", "SHOP-2602", result == null ? "The Currency response was empty." : result.ResultMessage + (string.IsNullOrWhiteSpace(result.StatusCode) ? "" : " (" + result.StatusCode + ")"), result == null ? "" : result.ErrorDetails);
            return true;
        }
        finally
        {
            CPH.UnsetGlobalVar(requestKey, false);
            CPH.UnsetGlobalVar(responseKey, false);
            string active = CPH.GetGlobalVar<string>(currentKey, false);
            if (string.Equals(active, requestId, StringComparison.Ordinal))
                CPH.UnsetGlobalVar(currentKey, false);
        }
    }

    private void Copy(CurrencyResponse r)
    {
        response.PreviousBalance = r.PreviousBalance;
        response.CurrencyChanged = r.AmountChanged;
        response.NewBalance = r.NewBalance;
    }

    private bool Resolve(Database db, out Listing l)
    {
        l = null;
        if (request.ItemId.Length > 0)
            db.Items.TryGetValue(Norm(request.ItemId), out l);
        if (l == null && request.ItemName.Length > 0)
            l = db.Items.Values.FirstOrDefault(x => Eq(x.DisplayName, request.ItemName));
        if (l == null)
            return Fail("ShopItemNotFound", "SHOP-1203", "The item is not registered in this shop.", request.ShopName);
        request.ItemId = l.ItemId;
        return true;
    }

    private bool Load(out Database d)
    {
        d = null;
        string p = PathFor();
        try
        {
            if (!File.Exists(p))
            {
                d = new Database
                {
                    SchemaVersion = SchemaVersion,
                    ShopName = request.ShopName,
                    Items = new Dictionary<string, Listing>(StringComparer.OrdinalIgnoreCase)
                };
                return true;
            }

            d = JsonConvert.DeserializeObject<Database>(File.ReadAllText(p));
            if (d == null || d.SchemaVersion != SchemaVersion)
                return Fail("LoadFailed", "SHOP-1601", "The shop database is invalid.", p);
            d.Items = new Dictionary<string, Listing>(d.Items ?? new Dictionary<string, Listing>(), StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch (Exception ex)
        {
            return Fail("LoadFailed", "SHOP-1602", "The shop database could not be loaded.", ex.Message);
        }
    }

    private bool Save(Database d)
    {
        string p = PathFor(), t = p + ".tmp", b = p + ".bak";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            d.UpdatedUtc = DateTime.UtcNow.ToString("o");
            File.WriteAllText(t, JsonConvert.SerializeObject(d, Formatting.Indented));
            if (File.Exists(p))
            {
                if (File.Exists(b))
                    File.Delete(b);
                File.Replace(t, p, b, true);
            }
            else
                File.Move(t, p);
            return true;
        }
        catch (Exception ex)
        {
            return Fail("SaveFailed", "SHOP-1603", "The shop database could not be saved.", ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(t))
                    File.Delete(t);
            }
            catch
            {
            }
        }
    }

    private string PathFor()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RootPath, request.ShopName, "shop.json");
    }

    private void Reset()
    {
        response = new Response
        {
            ActionResult = "NotExecuted",
            StatusCode = "SHOP-0000"
        };
        Outputs();
    }

    private void SetItem(Listing l)
    {
        response.ShopName = request.ShopName;
        response.ItemId = l.ItemId;
        response.ItemName = l.DisplayName;
        response.BuyPrice = l.BuyPrice;
        response.SellPrice = l.SellPrice;
        response.Maximum = l.Maximum;
        response.Enabled = l.Enabled;
        Outputs();
    }

    private void Success(string a, string m)
    {
        response.Success = true;
        response.ActionResult = a;
        response.StatusCode = "SHOP-0000";
        response.ResultMessage = m;
        response.ErrorDetails = "";
        Outputs();
        Send(m);
    }

    private bool Fail(string a, string c, string m, string d)
    {
        response.Success = false;
        response.ActionResult = a;
        response.StatusCode = c;
        response.ResultMessage = m;
        response.ErrorDetails = d ?? "";
        Outputs();
        Send(m);
        CPH.LogError("[Shop] " + c + " - " + m + " " + d);
        return false;
    }

    private void Outputs()
    {
        CPH.SetArgument("shopSuccess", response.Success);
        CPH.SetArgument("shopActionResult", response.ActionResult ?? "");
        CPH.SetArgument("shopStatusCode", response.StatusCode ?? "");
        CPH.SetArgument("shopResultMessage", response.ResultMessage ?? "");
        CPH.SetArgument("shopErrorDetails", response.ErrorDetails ?? "");
        CPH.SetArgument("shopName", response.ShopName ?? "");
        CPH.SetArgument("shopItemId", response.ItemId ?? "");
        CPH.SetArgument("shopItemName", response.ItemName ?? "");
        CPH.SetArgument("shopQuantity", response.Quantity);
        CPH.SetArgument("shopBuyPrice", response.BuyPrice);
        CPH.SetArgument("shopSellPrice", response.SellPrice);
        CPH.SetArgument("shopTotalPrice", response.TotalPrice);
        CPH.SetArgument("shopMaximumPerTransaction", response.Maximum);
        CPH.SetArgument("shopEnabled", response.Enabled);
        CPH.SetArgument("shopCanBuy", response.CanBuy);
        CPH.SetArgument("shopCanSell", response.CanSell);
        CPH.SetArgument("shopPreviousQuantity", response.PreviousQuantity);
        CPH.SetArgument("shopNewQuantity", response.NewQuantity);
        CPH.SetArgument("shopPreviousBalance", response.PreviousBalance);
        CPH.SetArgument("shopCurrencyChanged", response.CurrencyChanged);
        CPH.SetArgument("shopNewBalance", response.NewBalance);
        CPH.SetArgument("shopInventoryChanged", response.InventoryChanged);
        CPH.SetArgument("shopRollbackAttempted", response.RollbackAttempted);
        CPH.SetArgument("shopRollbackSuccessful", response.RollbackSuccessful);
    }

    private void Send(string m)
    {
        bool b = request == null || !request.SendMessage.HasValue ? DefaultSendMessage : request.SendMessage.Value;
        if (b && !string.IsNullOrWhiteSpace(m))
            CPH.SendMessage(m, true, true);
    }

    private string Text(string n)
    {
        object v;
        return CPH.TryGetArg(n, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
    }

    private bool? NullableBool(string n)
    {
        bool b;
        return bool.TryParse(Text(n), out b) ? (bool? )b : null;
    }

    private NumberState Number(string n, out long v)
    {
        v = 0;
        string s = Text(n).Trim();
        if (s.Length == 0 || (s.StartsWith("%") && s.EndsWith("%")))
            return NumberState.Missing;
        if (s.Contains(".") || s.Contains(",") || s.Contains("e") || s.Contains("E") || !long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < 0)
        {
            Fail("ValidationFailed", "SHOP-1302", n + " must be a non-negative Int64 whole number, but received '" + s + "'.", n + "='" + s + "'.");
            return NumberState.Invalid;
        }

        return NumberState.Valid;
    }

    private static string Norm(string s)
    {
        return (s ?? "").Trim().ToLowerInvariant();
    }

    private static bool Eq(string a, string b)
    {
        return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidName(string s)
    {
        return !string.IsNullOrWhiteSpace(s) && s.All(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_');
    }

    private class Request
    {
        public string Operation, ShopName, Platform, OwnerId, UserName, PointId, InventoryName, ItemId, ItemName;
        public long Quantity, BuyPrice, SellPrice, Maximum;
        public bool HasBuyPrice, HasSellPrice, HasMaximum;
        public bool? Enabled, SendMessage;
    }

    private class Response
    {
        public bool Success;
        public string ActionResult, StatusCode, ResultMessage, ErrorDetails, ShopName, ItemId, ItemName;
        public long Quantity, BuyPrice, SellPrice, TotalPrice, Maximum, PreviousQuantity, NewQuantity, PreviousBalance, CurrencyChanged, NewBalance;
        public bool Enabled, CanBuy, CanSell, InventoryChanged, RollbackAttempted, RollbackSuccessful;
    }

    private class InventoryResponse
    {
        public bool Success;
        public string ActionResult, StatusCode, ResultMessage, ErrorDetails, ItemId, ItemName;
        public long Quantity, RequestedQuantity, PreviousQuantity, NewQuantity;
        public bool HasItem, CanAdd, CanRemove, Changed;
    }

    private class CurrencyResponse
    {
        public bool Success;
        public string ActionResult, StatusCode, ResultMessage, ErrorDetails;
        public long RequestedAmount, PreviousBalance, AmountChanged, NewBalance;
        public bool CanAdd, CanRemove, OperationApplied;
    }

    private class Database
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion;
        [JsonProperty("shopName")]
        public string ShopName;
        [JsonProperty("updatedUtc")]
        public string UpdatedUtc;
        [JsonProperty("items")]
        public Dictionary<string, Listing> Items;
    }

    private class Listing
    {
        [JsonProperty("itemId")]
        public string ItemId;
        [JsonProperty("displayName")]
        public string DisplayName;
        [JsonProperty("buyPrice")]
        public long BuyPrice;
        [JsonProperty("sellPrice")]
        public long SellPrice;
        [JsonProperty("maximumPerTransaction")]
        public long Maximum;
        [JsonProperty("enabled")]
        public bool Enabled;
    }
}
