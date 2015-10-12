﻿namespace Economy.scripts.Messages
{
    using System;
    using System.Linq;
    using EconConfig;
    using Economy.scripts;
    using ProtoBuf;
    using Sandbox.Common.ObjectBuilders;
    using Sandbox.Definitions;
    using Sandbox.ModAPI;
    using Sandbox.ModAPI.Interfaces;
    using VRage;
    using VRage.ObjectBuilders;

    /// <summary>
    /// this is to do the actual work of checking and moving the goods.
    /// </summary>
    [ProtoContract]
    public class MessageSell : MessageBase
    {
        /// <summary>
        /// person, NPC, offer or faction to sell to
        /// </summary>
        [ProtoMember(1)]
        public string ToUserName;

        /// <summary>
        /// qty of item
        /// </summary>
        [ProtoMember(2)]
        public decimal ItemQuantity;

        /// <summary>
        /// item name / id we are selling
        /// </summary>
        [ProtoMember(3)]
        public string ItemTypeId;

        [ProtoMember(4)]
        public string ItemSubTypeName;

        /// <summary>
        /// unit price of item
        /// </summary>
        [ProtoMember(5)]
        public decimal ItemPrice;

        /// <summary>
        /// Use the Current Buy price to sell it at. The Player 
        /// will not have access to this information without fetching it first. This saves us the trouble.
        /// </summary>
        [ProtoMember(6)]
        public bool UseBankBuyPrice;

        /// <summary>
        /// We are selling to the Merchant.
        /// </summary>
        [ProtoMember(7)]
        public bool SellToMerchant;

        /// <summary>
        /// The Item is been put onto the market.
        /// </summary>
        [ProtoMember(8)]
        public bool OfferToMarket;

        //[ProtoMember(9)]
        //public string zone; //used to identify market we are selling to ??

        public static void SendMessage(string toUserName, decimal itemQuantity, string itemTypeId, string itemSubTypeName, decimal itemPrice, bool useBankBuyPrice, bool sellToMerchant, bool offerToMarket)
        {
            ConnectionHelper.SendMessageToServer(new MessageSell { ToUserName = toUserName, ItemQuantity = itemQuantity, ItemTypeId = itemTypeId, ItemSubTypeName = itemSubTypeName, ItemPrice = itemPrice, UseBankBuyPrice = useBankBuyPrice, SellToMerchant = sellToMerchant, OfferToMarket = offerToMarket });
        }

        public override void ProcessClient()
        {
            // never processed on client
        }

        public override void ProcessServer()
        {
            //* Logic:                     
            //* Get player steam ID
            var payingPlayer = MyAPIGateway.Players.FindPlayerBySteamId(SenderSteamId);

            MyPhysicalItemDefinition definition = null;
            MyObjectBuilderType result;
            if (MyObjectBuilderType.TryParse(ItemTypeId, out result))
            {
                var id = new MyDefinitionId(result, ItemSubTypeName);
                MyDefinitionManager.Static.TryGetPhysicalItemDefinition(id, out definition);
            }

            if (definition == null)
            {
                // Someone hacking, and passing bad data?
                MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "Sorry, the item you specified doesn't exist!");
                return;
            }

            // Do a floating point check on the item item. Tools and components cannot have decimals. They must be whole numbers.
            if (definition.Id.TypeId != typeof(MyObjectBuilder_Ore) && definition.Id.TypeId != typeof(MyObjectBuilder_Ingot))
            {
                if (ItemQuantity != Math.Truncate(ItemQuantity))
                {
                    MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "You must provide a whole number for the quantity of that item.");
                    return;
                }
                //ItemQuantity = Math.Round(ItemQuantity, 0);  // Or do we just round the number?
            }

            if (ItemQuantity <= 0)
            {
                MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "Invalid quantity, or you dont have any to trade!");
                return;
            }

            // Who are we selling to?
            BankAccountStruct accountToBuy;
            if (SellToMerchant)
                accountToBuy = EconomyScript.Instance.Data.Accounts.FirstOrDefault(a => a.SteamId == EconomyConsts.NpcMerchantId);
            else
                accountToBuy = AccountManager.FindAccount(ToUserName);

            if (accountToBuy == null)
            {
                MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "Sorry, player does not exist or have an account!");
                return;
            }

            var marketItem = EconomyScript.Instance.Data.MarketItems.FirstOrDefault(e => e.TypeId == ItemTypeId && e.SubtypeName == ItemSubTypeName);
            if (marketItem == null)
            {
                MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "Sorry, the items you are trying to sell doesn't have a market entry!");
                // TODO: in reality, this item needs not just to have an entry created, but a value applied also. It's the value that is more important.
                return;
            }

            if (marketItem.IsBlacklisted)
            {
                MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "Sorry, the item you tried to sell is blacklisted on this server.");
                return;
            }

            // Verify that the items are in the player inventory.
            // TODO: later check trade block, cockpit inventory, cockpit ship inventory, inventory of targeted cube.

            // Get the player's inventory, regardless of if they are in a ship, or a remote control cube.
            var character = payingPlayer.GetCharacter();
            // TODO: do players in Cryochambers count as a valid trading partner? They should be alive, but the connected player may be offline.
            // I think we'll have to do lower level checks to see if a physical player is Online.
            if (character == null)
            {
                // Player has no body. Could mean they are dead.
                // Either way, there is no inventory.
                MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "You are dead. You cannot trade while dead.");
                return;
            }

            // TODO: is a null check adaqaute?, or do we need to check for IsDead?
            // I don't think the chat console is accessible during respawn, only immediately after death.
            // Is it valid to be able to trade when freshly dead?
            //var identity = payingPlayer.Identity();
            //MyAPIGateway.Utilities.ShowMessage("CHECK", "Is Dead: {0}", identity.IsDead);

            //if (identity.IsDead)
            //{
            //    MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "You are dead. You cannot trade while dead.");
            //    return;
            //}

            var inventoryOwnwer = (IMyInventoryOwner)character;
            var inventory = (Sandbox.ModAPI.IMyInventory)inventoryOwnwer.GetInventory(0);
            MyFixedPoint amount = (MyFixedPoint)ItemQuantity;

            var storedAmount = inventory.GetItemAmount(definition.Id);
            if (amount > storedAmount)
            {
                // Insufficient items in inventory.
                // TODO: use of definition.GetDisplayName() isn't localized here.
                MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "You don't have {0} of '{1}' to sell. You have {2} in your inventory.", ItemQuantity, definition.GetDisplayName(), storedAmount);
                return;
            }

            if (UseBankBuyPrice)
                // The player is selling, but the *Market* will *buy* it from the player at this price.
                ItemPrice = marketItem.BuyPrice;

            var accountToSell = AccountManager.FindOrCreateAccount(SenderSteamId, SenderDisplayName, SenderLanguage);
            var transactionAmount = ItemPrice * ItemQuantity;

            // need fix negative amounts before checking if the player can afford it.
            if (!payingPlayer.IsAdmin())
                transactionAmount = Math.Abs(transactionAmount);

            if (SellToMerchant)// && (merchant has enough money  || !EconomyConsts.LimitedSupply)
                //this is also a quick fix ideally npc should buy what it can afford and the rest is posted as a sell offer
            {
                if (accountToBuy.BankBalance >= transactionAmount || !EconomyConsts.LimitedSupply)
                {
                    // here we look up item price and transfer items and money as appropriate
                    inventory.RemoveItemsOfType(amount, definition.Id);
                    marketItem.Quantity += ItemQuantity; // increment Market content.

                    accountToBuy.BankBalance -= transactionAmount;
                    accountToBuy.Date = DateTime.Now;

                    accountToSell.BankBalance += transactionAmount;
                    accountToSell.Date = DateTime.Now;
                    MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "You just sold {0} worth of {2} ({1} units)", transactionAmount, ItemQuantity, definition.GetDisplayName());
                }
                else
                {
                    MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "NPC can't afford {0} worth of {2} ({1} units) NPC only has {3} funds!", transactionAmount, ItemQuantity, definition.GetDisplayName(), accountToBuy.BankBalance);
                }
                return;
            }
            else if (OfferToMarket)
            {
                // TODO: Here we post offer to appropriate zone market

                return;
            }
            else
            {
                // is it a player then?             
                if (accountToBuy.SteamId == payingPlayer.SteamUserId)
                {
                    // commented out for testing with myself.
                    //MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "Sorry, you cannot sell to yourself!");
                    //return;
                }

                // check if buying player is online and in range?
                var buyingPlayer = MyAPIGateway.Players.FindPlayerBySteamId(accountToBuy.SteamId);

                if (EconomyConsts.LimitedRange && !Support.RangeCheck(buyingPlayer, payingPlayer))
                {
                    MessageClientTextMessage.SendMessage(SenderSteamId, "BUY", "Sorry, you are not in range of that player!");
                    return;
                }




                // write to Trade offer table.
                MarketManager.CreateTradeOffer(SenderSteamId, ItemTypeId, ItemSubTypeName, ItemQuantity, transactionAmount, accountToBuy.SteamId);

                // remove items from inventory.
                inventory.RemoveItemsOfType(amount, definition.Id);

                // if other player online, send message.
                if (buyingPlayer == null)
                {
                    // TODO: other player offline.
                    // TODO: we need a way to queue up messages.
                    // While you were gone....
                    // You missed an offer for 4000Kg of Gold for 20,000.
                }
                else
                {
                    // TODO: other player is online.
                    MessageClientTextMessage.SendMessage(accountToBuy.SteamId, "SELL", 
                        "You have received an offer from {0} to buy {1} {2} at price {3} - type '/sell accept' to accept offer (or '/sell deny' to reject and return ore to seller)", 
                        SenderDisplayName, ItemQuantity, definition.GetDisplayName(), transactionAmount);
                    MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "Your offer has been sent.");
                }
                // send message to seller to confirm action, "Your Trade offer has been submitted, and the goods removed from you inventory."

                // Later actions..
                // https://github.com/jpcsupplies/Economy_mod/issues/31
                // https://github.com/jpcsupplies/Economy_mod/issues/46
                // "/sell cancel"  to cancel trade offer. Did you mistype a number?  
                // Returned goods need to be queued.
                // if trade offer rejected, message back "Your Trade offer of xxx to yyy has been rejected."  if first item in queue, "Type '/return' to receive your goods back."
                // if trade offer times out, message back "Your Trade offer of xxx to yyy has timed."  if first item in queue, "Type '/return' to receive your goods back."
                // if trade offer accepted, finish funds trasfer. message back "Your Trade offer of xxx to yyy has been accepted. You have recieved zzzz"
            }

            // this is a fall through from the above conditions not yet complete.
            MessageClientTextMessage.SendMessage(SenderSteamId, "SELL", "Not yet complete.");
        }
    }
}