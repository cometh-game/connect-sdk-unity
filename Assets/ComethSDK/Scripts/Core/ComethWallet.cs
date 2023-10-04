using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using ComethSDK.Scripts.Adapters.Interfaces;
using ComethSDK.Scripts.HTTP;
using ComethSDK.Scripts.HTTP.Responses;
using ComethSDK.Scripts.Interfaces;
using ComethSDK.Scripts.Tools;
using ComethSDK.Scripts.Tools.Signers;
using ComethSDK.Scripts.Types;
using ComethSDK.Scripts.Types.MessageTypes;
using Nethereum.ABI.EIP712;
using Nethereum.Contracts;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.RPC.Eth.Transactions;
using Nethereum.Signer;
using Nethereum.Siwe.Core;
using Nethereum.Web3;
using UnityEngine;
using EventHandler = ComethSDK.Scripts.Tools.EventHandler;

namespace ComethSDK.Scripts.Core
{
	public class ComethWallet
	{
		private readonly API _api;
		private readonly IAuthAdaptor _authAdaptor;
		private readonly string _chainId;
		private readonly Uri _uri = new("https://api.cometh.finance");
		private readonly BigInteger BASE_GAS;
		private readonly double REWARD_PERCENTILE;
		private bool _connected;
		private EventHandler _eventHandler;
		private Constants.Network _network;

		private List<SponsoredAddressResponse.SponsoredAddress> _sponsoredAddresses = new();
		private string _walletAddress;
		private Web3 _web3;

		private Web3Auth _web3Auth;

		public ComethWallet(IAuthAdaptor authAdaptor, string apiKey)
		{
			_authAdaptor = authAdaptor;
			_chainId = authAdaptor.ChainId;
			_api = new API(apiKey, int.Parse(_chainId));
			BASE_GAS = Constants.DEFAULT_BASE_GAS;
			REWARD_PERCENTILE = Constants.DEFAULT_REWARD_PERCENTILE;
		}

		public async Task Connect()
		{
			if (_authAdaptor == null) throw new Exception("No EOA adapter found");

			if (!Constants.IsNetworkSupported(_chainId)) throw new Exception("This network is not supported");

			_web3 = new Web3(Constants.GetNetworkByChainID(_chainId).RPCUrl);

			await _authAdaptor.Connect();

			var account = _authAdaptor.GetAccount();
			var predictedWalletAddress = await _api.GetWalletAddress(account);
			_walletAddress = predictedWalletAddress ?? throw new Exception("Error while getting wallet address");

			var nonce = await _api.GetNonce(predictedWalletAddress);
			if (nonce == null) throw new Exception("Error while getting nonce");

			var message = CreateMessage(predictedWalletAddress, nonce);
			var messageToSign = SiweMessageStringBuilder.BuildMessage(message);
			var signatureSiwe = await SignMessage(messageToSign);

			//SAFE ADDRESS
			var walletAddress = await _api.ConnectToComethWallet(
				message,
				signatureSiwe,
				predictedWalletAddress
			);
			if (walletAddress == null) throw new Exception("Error while connecting to Cometh Wallet");

			_sponsoredAddresses = await _api.GetSponsoredAddresses();
			if (_sponsoredAddresses == null) throw new Exception("Error while getting sponsored addresses");

			_connected = true;
			_eventHandler = new EventHandler(_web3, _walletAddress);
		}

		public async Task<TransactionReceipt> Wait(string safeTxHash)
		{
			return await _eventHandler.Wait(safeTxHash);
		}

		public Contract GetContract(string abi, string address)
		{
			return _web3.Eth.GetContract(abi, address);
		}

		public bool GetConnected()
		{
			return _connected;
		}

		public UserInfos GetUserInfos()
		{
			if (_authAdaptor == null) throw new Exception("Cannot provide user infos");

			var userInfo = _authAdaptor.GetUserInfos();
			var userInfos = new UserInfos
			{
				email = userInfo.email,
				name = userInfo.name,
				profileImage = userInfo.profileImage,
				aggregateVerifier = userInfo.aggregateVerifier,
				verifier = userInfo.verifier,
				verifierId = userInfo.verifierId,
				typeOfLogin = userInfo.typeOfLogin,
				dappShare = userInfo.dappShare,
				idToken = userInfo.idToken,
				oAuthIdToken = userInfo.oAuthIdToken,
				oAuthAccessToken = userInfo.oAuthAccessToken,
				ownerAddress = _authAdaptor.GetAccount(),
				walletAddress = _walletAddress
			};
			return userInfos;
		}

		public string GetAddress()
		{
			return _walletAddress;
		}

		public async Task<BigInteger> GetBalance(string address)
		{
			return await _web3.Eth.GetBalance.SendRequestAsync(address);
		}

		public async Task Logout()
		{
			if (_authAdaptor == null) throw new Exception("No EOA adapter found");
			await _authAdaptor.Logout();
			_connected = false;
		}

		public async Task<string> AddOwner(string newOwner)
		{
			if (!_connected)
			{
				Debug.Log("Please Login First");
				return "";
			}

			var to = _walletAddress;
			const string value = "0";

			var contract = _web3.Eth.GetContract(Constants.SAFE_ABI, _walletAddress);
			var addOwnerWithThresholdFunction = contract.GetFunction("addOwnerWithThreshold");
			var data = addOwnerWithThresholdFunction.GetData(newOwner, 1);

			var safeTxHash = await SendTransaction(to, value, data);

			return safeTxHash;
		}

		public void CancelWaitingForEvent()
		{
			_eventHandler.CancelWait();
		}

		/**
		   * Signing Message Section
		   */
		public async Task<string> SignMessage(string message)
		{
			var typedData = new TypedData<DomainWithChainIdAndVerifyingContract>
			{
				Domain = new DomainWithChainIdAndVerifyingContract
				{
					ChainId = int.Parse(_chainId),
					VerifyingContract = _walletAddress
				},

				Types = new Dictionary<string, MemberDescription[]>
				{
					["EIP712Domain"] = new[]
					{
						new MemberDescription { Name = "chainId", Type = "uint256" },
						new MemberDescription { Name = "verifyingContract", Type = "address" }
					},
					["SafeMessage"] = new[]
					{
						new MemberDescription { Name = "message", Type = "bytes" }
					}
				},
				PrimaryType = "SafeMessage"
			};

			var ethereumMessageSigner = new EthereumMessageSigner();
			var messageBytes = Encoding.UTF8.GetBytes(message);
			var hashedMessage = ethereumMessageSigner.HashPrefixedMessage(messageBytes);

			var messageTyped = new SafeMessage
			{
				message = hashedMessage.ToHex().EnsureHexPrefix()
			};

			var signature = await SignTypedData(messageTyped, typedData);

			return signature;
		}

		/**
		 * Transaction Section
		 */
		public async Task<string> SendTransaction(string to, string value, string data)
		{
			if (!_connected)
			{
				Debug.Log("Please Login First");
				return "";
			}

			var nonce = await Tools.Utils.GetNonce(_web3, _walletAddress);
			var typedData = Tools.Utils.CreateSafeTxTypedData(_chainId, _walletAddress);
			var safeTx = Tools.Utils.CreateSafeTx(to, value, data, nonce);

			if (!ToSponsoredAddress(safeTx.to)) safeTx = await SetTransactionGas(safeTx);

			var txSignature = await SignTypedData(safeTx, typedData);

			Debug.Log("Sending Transaction");
			return await _api.RelayTransaction(new RelayTransactionType(
				safeTx, txSignature, _walletAddress)
			);
		}

		public async Task<GasEstimates> EstimateTransactionGas(ISafeTransactionDataPartial safeTxData)
		{
			var safeTxGas = safeTxData.safeTxGas;
			safeTxGas += await CalculateSafeTxGas(safeTxData.data, safeTxData.to);

			var gasPrice = safeTxData.gasPrice;
			gasPrice += await CalculateGasPrice();

			return new GasEstimates { safeTxGas = safeTxGas, baseGas = BASE_GAS, gasPrice = gasPrice };
		}

		public async Task<BigInteger> CalculateMaxFees(string to, string value, string data, int nonce)
		{
			var safeTx = Tools.Utils.CreateSafeTx(to, value, data, nonce);
			safeTx = await SetTransactionGas(safeTx);
			var totalGasCost = (safeTx.safeTxGas + safeTx.baseGas) * safeTx.gasPrice;
			return totalGasCost + BigInteger.Parse(value);
		}

		/**
		 * Private Methods
		 */
		private SiweMessage CreateMessage(string address, string nonce)
		{
			var domain = _uri.Host;
			var origin = _uri.Scheme + "://" + _uri.Host;
			const string statement = "Sign in with Ethereum to Cometh";

			var message = new SiweMessage
			{
				Domain = domain,
				Address = address,
				Statement = statement,
				Uri = origin,
				Version = "1",
				ChainId = _chainId,
				Nonce = nonce
			};

			message.SetIssuedAtNow();

			return message;
		}

		private async Task<string> SignTypedData<T, TDomain>(T message, TypedData<TDomain> typedData)
		{
			var signer = _authAdaptor.GetSigner();
			string signature;

			if (signer.GetType() == typeof(ComethAuthSigner))
			{
				var value = new Dictionary<string, object>();
				if (message.GetType() == typeof(SafeTx))
				{
					if (message is SafeTx safeTx)
					{
						value.Add("to", safeTx.to);
						value.Add("value", safeTx.value);
						value.Add("data", safeTx.data);
						value.Add("operation", safeTx.operation);
						value.Add("safeTxGas", safeTx.safeTxGas);
						value.Add("baseGas", safeTx.baseGas);
						value.Add("gasPrice", safeTx.gasPrice);
						value.Add("gasToken", safeTx.gasToken);
						value.Add("refundReceiver", safeTx.refundReceiver);
						value.Add("nonce", safeTx.nonce);
					}
					else
					{
						throw new Exception("Invalid SafeTx");
					}
				}
				else if (message.GetType() == typeof(SafeMessage))
				{
					if (message is SafeMessage safeMessage) value.Add("message", safeMessage.message);
				}

				if (typedData.Domain is not DomainWithChainIdAndVerifyingContract domain)
					throw new Exception("Invalid Domain");

				signature = await signer.SignTypedData(domain,
					typedData.Types,
					value);

				return signature;
			}

			signature = signer.SignTypedData(message, typedData);
			return signature;
		}

		private bool ToSponsoredAddress(string to)
		{
			//if index >= 0 then address is sponsored
			var index = _sponsoredAddresses.FindIndex(sponsoredAddress => sponsoredAddress.targetAddress == to.ToLower());
			return index >= 0;
		}

		private async Task<BigInteger> CalculateSafeTxGas(string data, string to)
		{
			var ethEstimateGas = new EthEstimateGas(_web3.Client);

			var transactionInput = new CallInput
			{
				Data = data,
				To = to,
				From = _walletAddress
			};
			return await ethEstimateGas.SendRequestAsync(transactionInput);
		}

		private async Task<BigInteger> CalculateGasPrice()
		{
			var ethFeeHistory = await _web3.Eth.FeeHistory.SendRequestAsync(
				new HexBigInteger(1),
				new BlockParameter(),
				new[] { REWARD_PERCENTILE });

			var reward = ethFeeHistory.Reward[0][0].Value;
			var baseFee = ethFeeHistory.BaseFeePerGas[0].Value;

			return reward + baseFee + (reward + baseFee) / 10;
		}

		private async Task<SafeTx> SetTransactionGas(SafeTx safeTxDataTyped)
		{
			var gasEstimates = await EstimateTransactionGas(safeTxDataTyped);
			safeTxDataTyped.safeTxGas = gasEstimates.safeTxGas;
			safeTxDataTyped.baseGas = gasEstimates.baseGas;
			safeTxDataTyped.gasPrice = gasEstimates.gasPrice;

			return safeTxDataTyped;
		}
	}
}