using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Helpers;

namespace WalletWasabi.Blockchain.TransactionProcessing
{
	public class TransactionProcessedResult
	{
		public SmartTransaction Transaction { get; }
		public bool IsWalletRelevant => SuccessfullyDoubleSpentCoins.Any() || ReplacedCoins.Any() || RestoredCoins.Any();
		public bool IsLikelyOwnCoinJoin { get; set; } = false;

		/// <summary>
		/// Coins those we previously had in the mempool, but this confirmed
		/// transaction has successfully invalidated them, because it spends
		/// some of the same inputs.
		/// </summary>
		public List<SmartCoin> SuccessfullyDoubleSpentCoins { get; set; } = new List<SmartCoin>();

		/// <summary>
		/// Unconfirmed coins those were replaced by the coins of the transaction.
		/// </summary>
		public List<SmartCoin> ReplacedCoins { get; set; } = new List<SmartCoin>();

		/// <summary>
		/// Coins those were made unspent again by this double spend transaction.
		/// </summary>
		public List<SmartCoin> RestoredCoins { get; set; } = new List<SmartCoin>();

		public TransactionProcessedResult(SmartTransaction transaction)
		{
			Transaction = Guard.NotNull(nameof(transaction), transaction);
		}
	}
}
