using System;
using System.Threading.Tasks;
using ComethSDK.Scripts.HTTP;
using ComethSDK.Scripts.Interfaces;
using Nethereum.GnosisSafe;
using Nethereum.Web3;

namespace ComethSDK.Scripts.Services
{
	public static class SafeService
	{
		public static async Task<bool> IsSigner(string signerAddress, string walletAddress, string provider, API api)
		{
			try
			{
				await IsDeployed(walletAddress, provider);

				var owner = await IsSafeOwner(walletAddress, signerAddress, provider);

				if (!owner) return false;
			}
			catch
			{
				var predictedWalletAddress = await api.GetWalletAddress(signerAddress);

				if (predictedWalletAddress != walletAddress) return false;
			}

			return true;
		}

		private static async Task<bool> IsSafeOwner(string walletAddress, string signerAddress, string provider)
		{
			var web3 = new Web3(provider);
			var service = new GnosisSafeService(web3, walletAddress);
			return await service.IsOwnerQueryAsync(signerAddress);
		}

		private static async Task IsDeployed(string walletAddress, string provider)
		{
			throw new NotImplementedException();
		}

		public static string GetFunctionSelector(IMetaTransactionData metaTransactionData)
		{
			return metaTransactionData.data.Substring(0, 10);
		}

		public static string GetTransactionsTotalValue(IMetaTransactionData[] safeTxData)
		{
			var txValue = 0;
			
			foreach (var safeTx in safeTxData)
			{
				txValue += int.Parse(safeTx.value);
			}
			
			return txValue.ToString();
		}
	}
}