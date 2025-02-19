using Microsoft.AspNetCore.Http;
using Cron.IO.Caching;
using Cron.IO.Data.LevelDB;
using Cron.IO.Json;
using Cron.Ledger;
using Cron.Network.P2P.Payloads;
using Cron.Network.RPC;
using Cron.Persistence;
using Cron.Persistence.LevelDB;
using Cron.Wallets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Snapshot = Cron.Persistence.Snapshot;

namespace Cron.Plugins
{
    public partial class RpcSystemAssetTrackerPlugin : Plugin, IPersistencePlugin, IRpcPlugin
    {
        private const byte SystemAssetUnspentCoinsPrefix = 0xfb;
        private const byte SystemAssetSpentUnclaimedCoinsPrefix = 0xfc;
        private const byte SystemAssetSentPrefix = 0xfd;
        private const byte SystemAssetReceivedPrefix = 0xfe;
        private DB _db;
        private DataCache<UserSystemAssetCoinOutputsKey, UserSystemAssetCoinOutputs> _userUnspentCoins;
        private bool _shouldTrackUnclaimed;
        private DataCache<UserSystemAssetCoinOutputsKey, UserSystemAssetCoinOutputs> _userSpentUnclaimedCoins;
        private bool _shouldTrackHistory;
        private DataCache<UserSystemAssetTransferKey, UserSystemAssetTransfer> _transfersSent;
        private DataCache<UserSystemAssetTransferKey, UserSystemAssetTransfer> _transfersReceived;
        private WriteBatch _writeBatch;
            //private int _maxResults;
        private uint _lastPersistedBlock;
        private bool _shouldPersistBlock;
        private Cron.IO.Data.LevelDB.Snapshot _levelDbSnapshot;

        
        public JObject OnProcess(HttpContext context, string method, JArray parameters)
        {
            if (_shouldTrackUnclaimed)
            {
                if (method == "getunclaimed") return ProcessGetUnclaimed(parameters);
            }

            if (method == "cron_send")
            {
                var res = Send(parameters);
                return res;
            }

            if (method == "cron_send_1xN")
            {
                return SendToMultipleSimple(parameters);
            }

            if (method == "cron_invoke_contract_as")
            {
                return InvokeSmartContractEntryPointAs(
                    parameters[0].AsString(),
                    parameters[1].AsString(),
                    parameters.Skip(2).ToArray());
            }

            if (method == "cron_create_address")
            {
                return this.CreateAddress();
            }

            if (method == "cron_get_address")
            {
                return GetAddress(parameters[0].AsString());
            }

            if (method == "cron_get_stat_special")
            {

                int code = (int)parameters[0].AsNumber();
                switch (code)
                {
                    case 0: return StatScenarioZero(parameters);
                    case 1: return Assets();
                }

                throw new Cron.Network.RPC.RpcException(-7171, "Wrong submethod code");
            }

            if (method == "cron_search_special")
            {
                int code = (int)parameters[0].AsNumber();
                switch (code)
                {
                    case 0: return SearchScenarioZero(parameters);
                }

                throw new Cron.Network.RPC.RpcException(-7171, "Wrong submethod code");
            }

            if (method == "cron_get_transactions")
            {
                return this.GetTransactions(addresses: parameters.Select(x => x.AsString()).ToList());
            }

            if (method == "cron_get_assets")
            {
                return this.Assets();
            }

            if (method == "cron_tx_block")
            {
                UInt256 txHash = UInt256.Parse(parameters[0].AsString());

                Transaction tx = Blockchain.Singleton.GetTransaction(txHash);
                uint? txBlock = Blockchain.Singleton.Store.GetTransactions().TryGet(txHash)?.BlockIndex;

                JObject jo = new JObject[] { txBlock, tx?.ToJson() };

                return jo;
            }

            if (method == "getunspents")
            {
                return this.ProcessGetUnspents(parameters);
            }
            
            if (method == "getaccountstate")
            {
                UInt160 script_hash = parameters[0].AsString().ToScriptHash();
                AccountState account = Blockchain.Singleton.Store.GetAccounts().TryGet(script_hash) ?? new AccountState(script_hash);
                return this.ProcessGetAccountState(parameters);
                
            }
            
            if (method == "getoutput")
            {
                UInt256 hash = UInt256.Parse(parameters[0].AsString());
                ushort index = ushort.Parse(parameters[1].AsString());
                return Blockchain.Singleton.Store.GetUnspent(hash, index)?.ToJson(index);
            }

            if (method == "relaytransaction")
            {
                var tx = Transaction.DeserializeFrom(parameters[0].AsString().HexToBytes());
                RelayResultReason reason = System.Blockchain.Ask<RelayResultReason>(tx).Result;
                if (reason == RelayResultReason.Succeed)
                {
                    var result = new JObject();
                    result["transaction"] = tx.ToJson();
                    return result;
                }

                return GetRelayResult(reason);
            }
            
            if (method == "gettransactionreceipt")
            {
                if (parameters[1] != null)
                { 
                    var parseResult = Int32.TryParse(parameters[1].AsString(), out int timeout);
                    if (parseResult)
                    {
                        Thread.Sleep(TimeSpan.FromMilliseconds(timeout / 10));
                        UInt256 hash = UInt256.Parse(parameters[0].AsString());

                            var trx =  Blockchain.Singleton.Store.GetTransactions().TryGet(hash);
                            var block = Blockchain.Singleton.Store.GetBlock(trx.BlockIndex);
                            var fullTransaction = block.Transactions.FirstOrDefault(f => f.Hash == trx.Transaction.Hash);
                            if (fullTransaction != null)
                            {
                                return fullTransaction.Data.ToJson();
                            }
                    }
                }

                return null;
            }

            if (method == "getclaimamount")
            {
                return ProcessGetClaimableSpents(parameters);
            }

            return null;
        }

        private JObject Assets()
        {
            var jassets = new JArray();

            using (var snapshot = Blockchain.Singleton.GetSnapshot())
            {
                var assets = snapshot.Assets.Find();
                var contracts = snapshot.Contracts.Find();
                foreach (var asset in assets)
                {
                    var jasset = new JObject();

                    jasset["id"] = asset.Key.ToString();
                    jasset["name"] = asset.Value.GetName();
                    jasset["type"] = asset.Value.AssetType;
                    jasset["amount"] = new JNumber((double)(decimal)asset.Value.Amount);
                    jasset["available"] = new JNumber((double)(decimal)asset.Value.Available);
                    jasset["issuer"] = asset.Value.Issuer.ToString();

                    jassets.Add(jasset);
                }
            }

            return jassets;
        }

        public void PostProcess(HttpContext context, string method, JArray _params, JObject result)
        {
        }

        public override void Configure()
        {
#if DEBUG
            var ce = Assembly.GetExecutingAssembly().CustomAttributes;
            string h = string.Join(Environment.NewLine, ce.Select
                (x => x.AttributeType.Name + ": "
                     + string.Join(", ", x.ConstructorArguments.Select(y => y.Value.ToString()))));

            var ver = ce.Where(x => x.AttributeType.Name == "AssemblyFileVersionAttribute")
                .FirstOrDefault()?
                .ConstructorArguments?.Select(y => y.Value.ToString())
                .FirstOrDefault();

            Console.WriteLine($"PID: {Process.GetCurrentProcess().Id} RpcSystemAssetTrackerPlugin v{ver}: Configure()");
            Console.WriteLine(h);
#endif


            if (_db == null)
            {
                var dbPath = GetConfiguration().GetSection("DBPath").Value ?? "SystemAssetBalanceData";
                _db = DB.Open(Path.GetFullPath(dbPath), new Options { CreateIfMissing = true });
                _shouldTrackUnclaimed = bool.TryParse(GetConfiguration().GetSection("TrackUnclaimed").Value, out bool shouldTrackUnclaimed) && shouldTrackUnclaimed;
                _shouldTrackHistory = bool.TryParse(GetConfiguration().GetSection("TrackHistory").Value, out bool shouldTrackHistory) && shouldTrackHistory;
                try
                {
                    _lastPersistedBlock = _db.Get(ReadOptions.Default, SystemAssetUnspentCoinsPrefix).ToUInt32();
                }
                catch (LevelDBException ex)
                {
                    if (!ex.Message.Contains("not found"))
                        throw;
                    _lastPersistedBlock = 0;
                }
            }
            //_maxResults = int.Parse(GetConfiguration().GetSection("MaxResults").Value ?? "1");
        }

        private void ResetBatch()
        {
            _writeBatch = new WriteBatch();
            _levelDbSnapshot?.Dispose();
            _levelDbSnapshot = _db.GetSnapshot();
            var dbOptions = new ReadOptions { FillCache = false, Snapshot = _levelDbSnapshot };
            _userUnspentCoins = new DbCache<UserSystemAssetCoinOutputsKey, UserSystemAssetCoinOutputs>(_db, dbOptions, _writeBatch, SystemAssetUnspentCoinsPrefix);
            if (_shouldTrackUnclaimed)
                _userSpentUnclaimedCoins = new DbCache<UserSystemAssetCoinOutputsKey, UserSystemAssetCoinOutputs>(_db, dbOptions, _writeBatch, SystemAssetSpentUnclaimedCoinsPrefix);
            if (_shouldTrackHistory)
            {
                _transfersSent = new DbCache<UserSystemAssetTransferKey, UserSystemAssetTransfer>(_db, dbOptions, _writeBatch, SystemAssetSentPrefix);
                _transfersReceived = new DbCache<UserSystemAssetTransferKey, UserSystemAssetTransfer>(_db, dbOptions, _writeBatch, SystemAssetReceivedPrefix);
            }
                
        }

        private bool ProcessBlock(Snapshot snapshot, Block block)
        {
            if (block.Transactions.Length <= 1)
            {
                _lastPersistedBlock = block.Index;
                return false;
            }

            ResetBatch();

            var r = snapshot.Assets.Find();

            var transactionsCache = snapshot.Transactions;
            foreach (Transaction tx in block.Transactions)
            {
                ushort outputIndex = 0;
                foreach (TransactionOutput output in tx.Outputs)
                {
                    byte idToken = GetTokenID(r, output.AssetId, transactionsCache);
                    bool isGoverningToken = output.AssetId.Equals(Blockchain.GoverningToken.Hash);
                    //  if (isGoverningToken || output.AssetId.Equals(Blockchain.UtilityToken.Hash))
                    if (idToken != 255)
                    {
                        // Add new unspent UTXOs by account script hash.
                        UserSystemAssetCoinOutputs outputs = _userUnspentCoins.GetAndChange(
                            new UserSystemAssetCoinOutputsKey(idToken, output.ScriptHash, tx.Hash),
                            () => new UserSystemAssetCoinOutputs());
                        outputs.AddTxIndex(outputIndex, output.Value);
                    }
                    outputIndex++;
                }

                // Iterate all input Transactions by grouping by common input hashes.
                foreach (var group in tx.Inputs.GroupBy(p => p.PrevHash))
                {
                    TransactionState txPrev = transactionsCache[group.Key];
                    // For each input being spent by this transaction.
                    foreach (CoinReference input in group)
                    {
                        // Get the output from the previous transaction that is now being spent.
                        var outPrev = txPrev.Transaction.Outputs[input.PrevIndex];

                        byte idToken = GetTokenID(r, outPrev.AssetId, transactionsCache);
                        bool isGoverningToken = outPrev.AssetId.Equals(Blockchain.GoverningToken.Hash);
                        // if (isGoverningToken || outPrev.AssetId.Equals(Blockchain.UtilityToken.Hash))
                        if (idToken != 255)
                        {
                            // Remove spent UTXOs for unspent outputs by account script hash.
                            var userCoinOutputsKey =
                                new UserSystemAssetCoinOutputsKey(idToken, outPrev.ScriptHash, input.PrevHash);
                            UserSystemAssetCoinOutputs outputs = _userUnspentCoins.GetAndChange(
                                userCoinOutputsKey, () => new UserSystemAssetCoinOutputs());
                            outputs.RemoveTxIndex(input.PrevIndex);
                            if (outputs.AmountByTxIndex.Count == 0)
                                _userUnspentCoins.Delete(userCoinOutputsKey);

                            if (_shouldTrackUnclaimed && isGoverningToken)
                            {
                                UserSystemAssetCoinOutputs spentUnclaimedOutputs = _userSpentUnclaimedCoins.GetAndChange(
                                    userCoinOutputsKey, () => new UserSystemAssetCoinOutputs());
                                spentUnclaimedOutputs.AddTxIndex(input.PrevIndex, outPrev.Value);
                            }
                        }
                    }
                }

                if (_shouldTrackUnclaimed && tx is ClaimTransaction claimTransaction)
                {
                    foreach (CoinReference input in claimTransaction.Claims)
                    {
                        TransactionState txPrev = transactionsCache[input.PrevHash];
                        var outPrev = txPrev.Transaction.Outputs[input.PrevIndex];

                        var claimedCoinKey =
                            new UserSystemAssetCoinOutputsKey(1, outPrev.ScriptHash, input.PrevHash);
                        UserSystemAssetCoinOutputs spentUnclaimedOutputs = _userSpentUnclaimedCoins.GetAndChange(
                            claimedCoinKey, () => new UserSystemAssetCoinOutputs());
                        spentUnclaimedOutputs.RemoveTxIndex(input.PrevIndex);
                        if (spentUnclaimedOutputs.AmountByTxIndex.Count == 0)
                            _userSpentUnclaimedCoins.Delete(claimedCoinKey);

                        if (snapshot.SpentCoins.TryGet(input.PrevHash)?.Items.Remove(input.PrevIndex) == true)
                            snapshot.SpentCoins.GetAndChange(input.PrevHash);
                    }
                }

                if (_shouldTrackHistory)
                {
                    // treat each input as a "sent"
                    for (int i = 0; i < tx.Inputs.Length; i++)
                    {
                        var input = tx.Inputs[i];
                        // find the previous tx
                        TransactionState prevTx = transactionsCache[input.PrevHash];
                        TransactionOutput prevOut = prevTx.Transaction.Outputs[input.PrevIndex];
                        if (prevOut.AssetId.Equals(Blockchain.GoverningToken.Hash) || prevOut.AssetId.Equals(Blockchain.UtilityToken.Hash))
                        {
                            _transfersSent.Add(new UserSystemAssetTransferKey(prevOut.ScriptHash, prevOut.AssetId, block.Timestamp, tx.Hash, (ushort)i),
                                new UserSystemAssetTransfer
                                {
                                    BlockIndex = block.Index,
                                    Amount = prevOut.Value
                                });
                        }
                    }

                    // treat each output as a "received"
                    for (int i = 0; i < tx.Outputs.Length; i++)
                    {
                        var output = tx.Outputs[i];
                        if (output.AssetId.Equals(Blockchain.GoverningToken.Hash) || output.AssetId.Equals(Blockchain.UtilityToken.Hash))
                        {
                            _transfersReceived.Add(new UserSystemAssetTransferKey(output.ScriptHash, output.AssetId, block.Timestamp, tx.Hash, (ushort)i),
                                new UserSystemAssetTransfer
                                {
                                    BlockIndex = block.Index,
                                    Amount = output.Value
                                });
                        }
                    }
                }
            }

            // Write the current height into the key of the prefix itself
            _writeBatch.Put(SystemAssetUnspentCoinsPrefix, block.Index);
            _lastPersistedBlock = block.Index;
            return true;
        }

        private byte GetTokenID(IEnumerable<KeyValuePair<UInt256, AssetState>> r,
            UInt256 assetId,
            DataCache<UInt256, TransactionState> transactionsCache)
        {
            if (assetId.Equals(Blockchain.UtilityToken.Hash))
                return 0;
            if (assetId.Equals(Blockchain.GoverningToken.Hash))
                return 1;

            var asset = r.Where(x => x.Key.Equals(assetId)).Select(x => x.Value).FirstOrDefault();
            if (asset == null)
                return 255;
            if (!EnsureCreatedTokenIdMap(r, asset, assetId, transactionsCache))
                return 255;

            return _dicTokenIds[assetId];
        }

        private bool EnsureCreatedTokenIdMap(
            IEnumerable<KeyValuePair<UInt256, AssetState>> assets,
            AssetState r, UInt256 id,
            DataCache<UInt256, TransactionState> transactionsCache)
        {
            if (_dicTokenIds.ContainsKey(id))
                return true;

            Dictionary<string, UInt256> L = new Dictionary<string, UInt256>();

            assets = assets.Where(x =>
               (!x.Key.Equals(Blockchain.GoverningToken.Hash))
            && (!x.Key.Equals(Blockchain.UtilityToken.Hash)));

            // Logger.Info($"== Preparing assets: {assets.Count()}");

            foreach (var s in assets)
            {
                // Logger.Info($"==== {s.Value.GetName()} {s.Key.ToString()}");
                var tx = transactionsCache.TryGet(s.Value.AssetId);
                if (tx != null)
                {
                    var f = tx.BlockIndex.ToString() + s.Value.AssetId.ToString();
                    L[f] = s.Value.AssetId;
                }
            }

            var LKS = L.Keys.ToList();
            LKS.Sort();

            byte baseTokenID = 2;
            foreach (var k in LKS)
            {
                var aid = L[k];
                if (baseTokenID > 254)
                    return false;       // still only 255 token IDs available
                if (id.Equals(aid))
                {
                    // Logger.Info($"=== Adding {aid.ToString()} with id = {baseTokenID}");
                    _dicTokenIds[aid] = baseTokenID;
                    return true;
                }
                baseTokenID++;
            }
            return false;
        }

        private Dictionary<UInt256, byte> _dicTokenIds = new Dictionary<UInt256, byte>();

        private void ProcessSkippedBlocks(Snapshot snapshot)
        {
            for (uint blockIndex = _lastPersistedBlock + 1; blockIndex < snapshot.PersistingBlock.Index; blockIndex++)
            {
                var skippedBlock = Blockchain.Singleton.Store.GetBlock(blockIndex);
                if (skippedBlock.Transactions.Length <= 1)
                {
                    _lastPersistedBlock = skippedBlock.Index;
                    continue;
                }

                _shouldPersistBlock = ProcessBlock(snapshot, skippedBlock);
                OnCommit(snapshot);
            }
        }

        public void OnPersist(Snapshot snapshot, IReadOnlyList<Blockchain.ApplicationExecuted> applicationExecutedList)
        {
            if (snapshot.PersistingBlock.Index > _lastPersistedBlock + 1)
                ProcessSkippedBlocks(snapshot);

            _shouldPersistBlock = ProcessBlock(snapshot, snapshot.PersistingBlock);
        }

        public void OnCommit(Snapshot snapshot)
        {
            if (!_shouldPersistBlock) return;
            _userUnspentCoins.Commit();
            if (_shouldTrackUnclaimed) _userSpentUnclaimedCoins.Commit();
            if (_shouldTrackHistory)
            {
                _transfersSent.Commit();
                _transfersReceived.Commit();
            }
            _db.Write(WriteOptions.Default, _writeBatch);
        }

        public bool ShouldThrowExceptionFromCommit(Exception ex)
        {
            return true;
        }

        public void PreProcess(HttpContext context, string method, JArray _params)
        {
        }

        private UInt160 GetScriptHashFromParam(string addressOrScriptHash)
        {
            return addressOrScriptHash.Length < 40 ?
                addressOrScriptHash.ToScriptHash() : UInt160.Parse(addressOrScriptHash);
        }

        private long GetSysFeeAmountForHeight(DataCache<UInt256, BlockState> blocks, uint height)
        {
            return blocks.TryGet(Blockchain.Singleton.GetBlockHash(height)).SystemFeeAmount;
        }

        private void CalculateClaimable(Snapshot snapshot, Fixed8 value, uint startHeight, uint endHeight, out Fixed8 generated, out Fixed8 sysFee)
        {
            uint amount = 0;
            uint ustart = startHeight / Blockchain.DecrementInterval;
            if (ustart < Blockchain.GenerationAmount.Length)
            {
                uint istart = startHeight % Blockchain.DecrementInterval;
                uint uend = endHeight / Blockchain.DecrementInterval;
                uint iend = endHeight % Blockchain.DecrementInterval;
                if (uend >= Blockchain.GenerationAmount.Length)
                {
                    uend = (uint)Blockchain.GenerationAmount.Length;
                    iend = 0;
                }
                if (iend == 0)
                {
                    uend--;
                    iend = Blockchain.DecrementInterval;
                }
                while (ustart < uend)
                {
                    amount += (Blockchain.DecrementInterval - istart) * Blockchain.GenerationAmount[ustart];
                    ustart++;
                    istart = 0;
                }
                amount += (iend - istart) * Blockchain.GenerationAmount[ustart];
            }

            Fixed8 fractionalShare = value / 100000000;
            generated = fractionalShare * amount;
            sysFee = fractionalShare * (GetSysFeeAmountForHeight(snapshot.Blocks, endHeight - 1) -
                     (startHeight == 0 ? 0 : GetSysFeeAmountForHeight(snapshot.Blocks, startHeight - 1)));
        }

        private bool AddClaims(JArray claimableOutput, ref Fixed8 runningTotal,
            Snapshot snapshot, DataCache<UInt256, SpentCoinState> storeSpentCoins,
            KeyValuePair<UserSystemAssetCoinOutputsKey, UserSystemAssetCoinOutputs> claimableInTx)
        {
            foreach (var claimTransaction in claimableInTx.Value.AmountByTxIndex)
            {
                var utxo = new JObject();
                var txId = claimableInTx.Key.TxHash.ToString().Substring(2);
                utxo["txid"] = txId;
                utxo["n"] = claimTransaction.Key;
                var spentCoinState = storeSpentCoins.TryGet(claimableInTx.Key.TxHash);
                var startHeight = spentCoinState.TransactionHeight;
                var endHeight = spentCoinState.Items[claimTransaction.Key];
                CalculateClaimable(snapshot, claimTransaction.Value, startHeight, endHeight, out var generated,
                    out var sysFee);
                var unclaimed = generated + sysFee;
                utxo["value"] = (double)(decimal)claimTransaction.Value;
                utxo["start_height"] = startHeight;
                utxo["end_height"] = endHeight;
                utxo["generated"] = (double)(decimal)generated;
                utxo["sys_fee"] = (double)(decimal)sysFee;
                utxo["unclaimed"] = (double)(decimal)unclaimed;
                runningTotal += unclaimed;
                claimableOutput.Add(utxo);
            }

            return true;
        }

        private JObject ProcessGetClaimableSpents(JArray parameters)
        {
            // UInt160 scriptHash =  GetScriptHashFromParam(parameters[0].AsString());
            UInt256 scriptHash = UInt256.Parse(parameters[0].AsString());
            var dbCache = new DbCache<UserSystemAssetCoinOutputsKey, UserSystemAssetCoinOutputs>(
                _db, null, null, SystemAssetSpentUnclaimedCoinsPrefix);

            JObject json = new JObject();
            JArray claimable = new JArray();
            json["claimable"] = claimable;
            //json["address"] = scriptHash.ToAddress();

            Fixed8 totalUnclaimed = Fixed8.Zero;
            using (Snapshot snapshot = Blockchain.Singleton.GetSnapshot())
            {
                var storeSpentCoins = snapshot.SpentCoins;
                byte[] prefix = new[] { (byte)1 }.Concat(scriptHash.ToArray()).ToArray();
                foreach (var claimableInTx in dbCache.Find(prefix))
                    if (!AddClaims(claimable, ref totalUnclaimed, snapshot, storeSpentCoins,
                        claimableInTx))
                        break;
            }
            json["unclaimed"] = (double)(decimal)totalUnclaimed;
            return json;
        }

        private JArray ProcessGetUnclaimedTransactions( UInt160 scriptHash)
        {
            JArray json = new JArray();

            var unspentsCache = new DbCache<UserSystemAssetCoinOutputsKey, UserSystemAssetCoinOutputs>(
                _db, null, null, SystemAssetUnspentCoinsPrefix);
            using (Snapshot snapshot = Blockchain.Singleton.GetSnapshot())
            {
                byte[] prefix = new[] { (byte)1 }.Concat(scriptHash.ToArray()).ToArray();

                var transactionsCache = snapshot.Transactions;
                foreach (var claimableInTx in unspentsCache.Find(prefix))
                {
                    var transaction = transactionsCache.TryGet(claimableInTx.Key.TxHash);
                    var obj = new JObject();
                    obj["txid"] = transaction.Transaction.Hash.ToString();
                    obj["vout"] = transaction.Transaction.Outputs.Select((p, i) => p.ToJson((ushort)i)).Count();
                    json.Add(obj);
                }
            }
            return json;
        }
        private JObject ProcessGetUnclaimed(JArray parameters)
        {
            UInt160 scriptHash = GetScriptHashFromParam(parameters[0].AsString());
            JObject json = new JObject();

            Fixed8 available = Fixed8.Zero;
            Fixed8 unavailable = Fixed8.Zero;
            var spentsCache = new DbCache<UserSystemAssetCoinOutputsKey, UserSystemAssetCoinOutputs>(
                _db, null, null, SystemAssetSpentUnclaimedCoinsPrefix);
            var unspentsCache = new DbCache<UserSystemAssetCoinOutputsKey, UserSystemAssetCoinOutputs>(
                _db, null, null, SystemAssetUnspentCoinsPrefix);
            using (Snapshot snapshot = Blockchain.Singleton.GetSnapshot())
            {
                var storeSpentCoins = snapshot.SpentCoins;
                byte[] prefix = new[] { (byte)1 }.Concat(scriptHash.ToArray()).ToArray();
                foreach (var claimableInTx in spentsCache.Find(prefix))
                {
                    var spentCoinState = storeSpentCoins.TryGet(claimableInTx.Key.TxHash);
                    foreach (var claimTxIndex in claimableInTx.Value.AmountByTxIndex)
                    {
                        var startHeight = spentCoinState.TransactionHeight;
                        var endHeight = spentCoinState.Items[claimTxIndex.Key];
                        CalculateClaimable(snapshot, claimTxIndex.Value, startHeight, endHeight, out var generated,
                            out var sysFee);
                        available += generated + sysFee;
                    }
                }

                var transactionsCache = snapshot.Transactions;
                foreach (var claimableInTx in unspentsCache.Find(prefix))
                {
                    var transaction = transactionsCache.TryGet(claimableInTx.Key.TxHash);

                    foreach (var claimTxIndex in claimableInTx.Value.AmountByTxIndex)
                    {
                        var startHeight = transaction.BlockIndex;
                        var endHeight = Blockchain.Singleton.Height;
                        CalculateClaimable(snapshot, claimTxIndex.Value, startHeight, endHeight,
                            out var generated,
                            out var sysFee);
                        unavailable += generated + sysFee;
                    }
                }
            }

            json["available"] = (double)(decimal)available;
            json["unavailable"] = (double)(decimal)unavailable;
            json["unclaimed"] = (double)(decimal)(available + unavailable);
            return json;
        }

        private bool AddUnspents(JArray unspents, ref Fixed8 runningTotal,
            KeyValuePair<UserSystemAssetCoinOutputsKey, UserSystemAssetCoinOutputs> unspentInTx)
        {
            var txId = unspentInTx.Key.TxHash.ToString().Substring(2);
            foreach (var unspent in unspentInTx.Value.AmountByTxIndex)
            {
                var utxo = new JObject();
                utxo["txid"] = txId;
                utxo["n"] = unspent.Key;
                utxo["value"] = new JNumber((double)(decimal)unspent.Value);
                runningTotal += unspent.Value;

                unspents.Add(utxo);
                // if (unspents.Count > _maxResults)
                //     return false;
            }
            return true;
        }
        
        private bool AddAccountUnspents(JArray unspents, ref Fixed8 runningTotal,
            KeyValuePair<UserSystemAssetCoinOutputsKey, UserSystemAssetCoinOutputs> unspentInTx)
        {
            var txId = unspentInTx.Key.TxHash.ToString().Substring(2);
            foreach (var unspent in unspentInTx.Value.AmountByTxIndex)
            {
                var utxo = new JObject();
                utxo["txid"] = txId;
                utxo["vout"] = unspent.Key;
                runningTotal += unspent.Value;

                unspents.Add(utxo);
                // if (unspents.Count > _maxResults)
                //     return false;
            }

            return true;
        }

        private JObject ProcessGetUnspents(JArray _params)
        {
            UInt160 scriptHash = GetScriptHashFromParam(_params[0].AsString());
            byte startingToken = 0; // 0 = Utility Token (CRON), 1 = Governing Token (CRONIUM)
            int maxIterations = 2;

            UInt256 th = UInt256.Zero;

            if (_params.Count > 1)
            {
                string gh = _params[1].AsString();
                bool isGoverningToken = (gh == "yes");
                bool isUtilityToken = (gh == "util");
                if (isGoverningToken)
                {
                    startingToken = 1;
                    maxIterations = 1;
                }
                else if (isUtilityToken)
                {
                    startingToken = 0;
                    maxIterations = 1;
                }

                if (_params.Count > 2)
                {
                    th = ParseTokenHash(_params[2].AsString());
                    if (th.Equals(Blockchain.UtilityToken.Hash))
                        th = UInt256.Zero;
                }
            }

            var unspentsCache = new DbCache<UserSystemAssetCoinOutputsKey, UserSystemAssetCoinOutputs>(
                _db, null, null, SystemAssetUnspentCoinsPrefix);

            string[] nativeAssetNames = { "CRON", "CRONIUM" };
            UInt256[] nativeAssetIds = { Blockchain.UtilityToken.Hash, Blockchain.GoverningToken.Hash };

            byte tokenId = 255;
            var snapshot = Blockchain.Singleton.GetSnapshot();
            var r = snapshot.Assets.Find();
            var txs = snapshot.Transactions;
            if (!th.Equals(UInt256.Zero))
            {
                tokenId = GetTokenID(r, th, txs);
                if (tokenId != 255)
                {
                    nativeAssetNames[1] = snapshot.Assets.TryGet(th).GetName();
                    nativeAssetIds[1] = th;
                }
                else
                {
                    throw new Cron.Network.RPC.RpcException(-7166, "Token not found");
                }
            }

            JObject json = new JObject();
            JArray balances = new JArray();
            json["balance"] = balances;
            json["address"] = scriptHash.ToAddress();
            for (byte tokenIndex = startingToken; maxIterations-- > 0; tokenIndex++)
            {
                byte[] prefix = new[] { (tokenId == 255 || tokenIndex == 0 ? tokenIndex : tokenId) }.Concat(scriptHash.ToArray()).ToArray();

                var unspents = new JArray();
                Fixed8 total = new Fixed8(0);

                foreach (var unspentInTx in unspentsCache.Find(prefix))
                    if (!AddUnspents(unspents, ref total, unspentInTx)) break;

                if (unspents.Count <= 0) continue;

                var balance = new JObject();
                balance["unspent"] = unspents;
                balance["asset_hash"] = nativeAssetIds[tokenIndex].ToString().Substring(2);
                balance["asset_symbol"] = balance["asset"] = nativeAssetNames[tokenIndex];
                balance["amount"] = new JNumber((double)(decimal)total); ;
                balances.Add(balance);
            }

            return json;
        }
        
        private JObject ProcessGetAccountState(JArray _params)
        {
            UInt160 scriptHash = GetScriptHashFromParam(_params[0].AsString());
            var snapshot = Blockchain.Singleton.GetSnapshot();
            var assets = snapshot.Assets.Find().ToList();
            var txs = snapshot.Transactions;
            var unspentsCache = new DbCache<UserSystemAssetCoinOutputsKey, UserSystemAssetCoinOutputs>(
                _db, null, null, SystemAssetUnspentCoinsPrefix);

            JObject json = new JObject();
          
            JArray unpsentsArray = new JArray();
            json["unspent"] = unpsentsArray;
            json["unclaimed"] =  ProcessGetUnclaimedTransactions(scriptHash);
            json["version"] = "0";
            json["votes"] = new JArray();
            json["frozen"] = false; // TODO : get from blockchain state
            json["script_hash"] = scriptHash.ToString();

            foreach (var asset in assets)
            {
                var th = asset.Key;
                
                var  tokenId = GetTokenID(assets, th, txs);
                byte[] prefix = new[] {tokenId}.Concat(scriptHash.ToArray()).ToArray();
               
                var unspents = new JArray();
                Fixed8 total = new Fixed8(0);
                var unpsentsInTxs = unspentsCache.Find(prefix);
                foreach (var unspentInTx in unpsentsInTxs)
                    if (!AddAccountUnspents(unspents, ref total, unspentInTx))
                        break;
               
                foreach (var tmp in unspents)
                {
                    unpsentsArray.Add(tmp);
                }
            }
            
            AccountState account = Blockchain.Singleton.Store.GetAccounts().TryGet(scriptHash) ??
                                   new AccountState(scriptHash);
            
            json["balances"] = account.Balances.Select(p =>
            {
                JObject balance = new JObject();
                balance["asset"] = p.Key.ToString();
                balance["value"] = p.Value.ToString();
                return balance;
            }).ToArray();
            

            return json;
        }

        private JObject ProcessGetUtxoTransfers(JArray _params)
        {
            UInt160 userScriptHash = GetScriptHashFromParam(_params[0].AsString());
            DateTime now = DateTime.UtcNow;
            DateTime sevenDaysAgo = now - TimeSpan.FromDays(7);
            List<UInt256> tokens = new List<UInt256>() { Blockchain.GoverningToken.Hash, Blockchain.UtilityToken.Hash };
            uint start, end;
            if (_params.Count > 1)
            {
                if (!uint.TryParse(_params[1].AsString(), out start))
                {   // cron or gas
                    if (_params[1].AsString().ToLower() == "cron")
                        tokens.Remove(Blockchain.UtilityToken.Hash);
                    else if (_params[1].AsString().ToLower() == "gas")
                        tokens.Remove(Blockchain.GoverningToken.Hash);
                    else
                        throw new RpcException(-32602, "Invalid params");
                    start = _params.Count > 2 ? (uint)_params[2].AsNumber() : sevenDaysAgo.ToTimestamp();
                    end = _params.Count > 3 ? (uint)_params[3].AsNumber() : now.ToTimestamp();
                }
                else
                {
                    start = (uint)_params[1].AsNumber();
                    end = _params.Count > 2 ? (uint)_params[2].AsNumber() : now.ToTimestamp();
                }
            }
            else
            {
                start = sevenDaysAgo.ToTimestamp();
                end = now.ToTimestamp();
            }
            if (end < start) throw new RpcException(-32602, "Invalid params");

            JObject json = new JObject();
            JArray transfersSent = new JArray();
            json["address"] = userScriptHash.ToAddress();
            json["sent"] = transfersSent;
            JArray transfersReceived = new JArray();
            json["received"] = transfersReceived;
            foreach (var assetId in tokens)
            {
                AddTransfers(SystemAssetSentPrefix, userScriptHash, assetId, start, end, transfersSent);
                AddTransfers(SystemAssetReceivedPrefix, userScriptHash, assetId, start, end, transfersReceived);
            }
            return json;
        }

        private void AddTransfers(byte dbPrefix, UInt160 userScriptHash, UInt256 assetId, uint startTime, uint endTime, JArray parentJArray)
        {
            var prefix = new[] { dbPrefix }.Concat(userScriptHash.ToArray()).Concat(assetId.ToArray()).ToArray();
            var startTimeBytes = BitConverter.GetBytes(startTime);
            var endTimeBytes = BitConverter.GetBytes(endTime);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(startTimeBytes);
                Array.Reverse(endTimeBytes);
            }

            var transferPairs = _db.FindRange<UserSystemAssetTransferKey, UserSystemAssetTransfer>(
                prefix.Concat(startTimeBytes).ToArray(),
                prefix.Concat(endTimeBytes).ToArray());

            Fixed8 sum = Fixed8.Zero;
            JArray transfers = new JArray();
            JObject group = new JObject();
            group["asset_hash"] = assetId.ToString();
            group["asset"] = assetId == Blockchain.GoverningToken.Hash ? "CRON" : assetId == Blockchain.UtilityToken.Hash ? "GAS" : throw new NotSupportedException();

            int resultCount = 0;
            foreach (var pair in transferPairs)
            {
                //if (++resultCount > _maxResults) break;
                JObject transfer = new JObject();
                transfer["block_index"] = pair.Value.BlockIndex;
                transfer["timestamp"] = pair.Key.Timestamp;
                transfer["txid"] = pair.Key.TxId.ToString();
                transfer["amount"] = pair.Value.Amount.ToString();
                sum += pair.Value.Amount;
                transfers.Add(transfer);
            }
            group["total_amount"] = sum.ToString();
            group["transactions"] = transfers;

            parentJArray.Add(group);
        }
    }
}
