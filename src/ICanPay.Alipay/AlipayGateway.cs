﻿using ICanPay.Core;
using ICanPay.Core.Exceptions;
using ICanPay.Core.Request;
using ICanPay.Core.Utils;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ICanPay.Alipay
{
    /// <summary>
    /// 支付宝网关
    /// </summary>
    public sealed class AlipayGateway
        : GatewayBase,
        IFormPayment, IUrlPayment, IAppPayment, IScanPayment, IBarcodePayment, IAppletPayment,
        IQuery, ICancel, IClose, IBillDownload, IRefund, IRefundQuery
    {

        #region 私有字段

        private readonly Merchant _merchant;

        #endregion

        #region 构造函数

        /// <summary>
        /// 初始化支付宝网关
        /// </summary>
        /// <param name="merchant">商户数据</param>
        public AlipayGateway(Merchant merchant)
            : base(merchant)
        {
            _merchant = merchant;
        }

        #endregion

        #region 属性

        public override string GatewayUrl { get; set; } = "https://openapi.alipay.com";

        private string RequestUrl => GatewayUrl + "gateway.do?charset=UTF-8";

        public new Merchant Merchant => _merchant;

        public new Order Order
        {
            get => (Order)base.Order;
            set => base.Order = value;
        }

        public new Notify Notify => (Notify)base.Notify;

        protected override bool IsWaitPay => Notify.TradeStatus == Constant.WAIT_BUYER_PAY;

        protected override bool IsSuccessPay => Notify.TradeStatus == Constant.TRADE_SUCCESS;

        protected override string[] NotifyVerifyParameter => new string[]
        {
            Constant.APP_ID,Constant.VERSION, Constant.CHARSET,
            Constant.TRADE_NO, Constant.SIGN, Constant.SIGN_TYPE
        };

        #endregion

        #region 方法

        #region 表单支付

        public string BuildFormPayment()
        {
            InitFormPayment();

            return GatewayData.ToForm(RequestUrl);
        }

        public void InitFormPayment()
        {
            Merchant.Method = Constant.WEB;
            Order.ProductCode = Constant.FAST_INSTANT_TRADE_PAY;

            InitOrderParameter();
        }

        #endregion

        #region Url支付

        public string BuildUrlPayment()
        {
            InitUrlPayment();

            return $"{RequestUrl}&{GetPaymentQueryString()}";
        }

        public void InitUrlPayment()
        {
            Merchant.Method = Constant.WAP;
            Order.ProductCode = Constant.QUICK_WAP_WAY;

            InitOrderParameter();
        }

        #endregion

        #region App支付

        public string BuildAppPayment()
        {
            InitAppPayment();

            return GetPaymentQueryString();
        }

        public void InitAppPayment()
        {
            Merchant.Method = Constant.APP;
            Order.ProductCode = Constant.QUICK_MSECURITY_PAY;

            InitOrderParameter();
        }

        #endregion

        #region 扫码支付

        public string BuildScanPayment()
        {
            PreCreate();

            return Notify.QrCode;
        }

        /// <summary>
        /// 预创建订单
        /// </summary>
        /// <returns></returns>
        private void PreCreate()
        {
            InitScanPayment();

            Commit(Constant.ALIPAY_TRADE_PRECREATE_RESPONSE);
        }

        public void InitScanPayment()
        {
            Merchant.Method = Constant.SCAN;

            InitOrderParameter();
        }

        #endregion

        #region 条码支付

        public void BuildBarcodePayment()
        {
            InitBarcodePayment();

            Commit(Constant.ALIPAY_TRADE_PAY_RESPONSE);

            if (Notify.Code == "10000")
            {
                OnPaymentSucceed(new PaymentSucceedEventArgs(this));
                return;
            }

            if (!string.IsNullOrEmpty(Notify.TradeNo))
            {
                Task.Run(async () =>
                {
                    await PollQueryTradeStateAsync(new Auxiliary
                    {
                        TradeNo = Notify.TradeNo
                    });
                })
                .GetAwaiter()
                .GetResult();
            }

            OnPaymentFailed(new PaymentFailedEventArgs(this)
            {
                Message = Notify.SubMessage
            });
        }

        public void InitBarcodePayment()
        {
            Merchant.Method = Constant.BARCODE;
            Order.ProductCode = Constant.FACE_TO_FACE_PAYMENT;

            InitOrderParameter();
        }

        /// <summary>
        /// 每隔5秒轮询判断用户是否支付,总共轮询5次
        /// </summary>
        private void PollQueryTradeState(IAuxiliary auxiliary)
        {
            for (int i = 0; i < 5; i++)
            {
                Thread.Sleep(5000);
                BuildQuery(auxiliary);
                if (IsSuccessPay)
                {
                    OnPaymentSucceed(new PaymentSucceedEventArgs(this));
                    return;
                }
            }

            BuildCancel(auxiliary);
            OnPaymentFailed(new PaymentFailedEventArgs(this)
            {
                Message = "支付超时"
            });
        }

        /// <summary>
        /// 异步每隔5秒轮询判断用户是否支付,总共轮询5次
        /// </summary>
        private async Task PollQueryTradeStateAsync(IAuxiliary auxiliary)
        {
            await Task.Run(() => PollQueryTradeState(auxiliary));
        }

        #endregion

        #region 小程序支付

        public string BuildAppletPayment()
        {
            return BuildAppPayment();
        }

        public void InitAppletPayment()
        {
            //与InitAppPayment不需要再写
        }

        #endregion

        #region 查询订单

        public void InitQuery(IAuxiliary auxiliary)
        {
            InitAuxiliaryParameter(GatewayAuxiliaryType.Query, auxiliary);
        }

        /// <summary>
        /// 查询订单
        /// </summary>
        public INotify BuildQuery(IAuxiliary auxiliary)
        {
            InitQuery(auxiliary);

            Commit(Constant.ALIPAY_TRADE_QUERY_RESPONSE);

            return Notify;
        }

        #endregion

        #region 撤销订单

        public void InitCancel(IAuxiliary auxiliary)
        {
            InitAuxiliaryParameter(GatewayAuxiliaryType.Cancel, auxiliary);
        }

        /// <summary>
        /// 撤销订单
        /// </summary>
        public INotify BuildCancel(IAuxiliary auxiliary)
        {
            InitCancel(auxiliary);

            Commit(Constant.ALIPAY_TRADE_CANCEL_RESPONSE);

            return Notify;
        }

        #endregion

        #region 关闭订单

        public INotify BuildClose(IAuxiliary auxiliary)
        {
            InitClose(auxiliary);

            Commit(Constant.ALIPAY_TRADE_CLOSE_RESPONSE);

            return Notify;
        }

        public void InitClose(IAuxiliary auxiliary)
        {
            InitAuxiliaryParameter(GatewayAuxiliaryType.Close, auxiliary);
        }

        #endregion

        #region 对账单下载

        public FileStream BuildBillDownload(IAuxiliary auxiliary)
        {
            InitBillDownload(auxiliary);

            Commit(Constant.ALIPAY_DATA_DATASERVICE_BILL_DOWNLOADURL_QUERY_RESPONSE);

            GatewayData.FromUrl(Notify.BillDownloadUrl);

            return HttpUtil.Download(Notify.BillDownloadUrl, $"{DateTime.Now.ToString(TIMEFORMAT)}.{GatewayData.GetStringValue(Constant.FILETYPE)}");
        }

        public void InitBillDownload(IAuxiliary auxiliary)
        {
            Merchant.Method = Constant.BILLDOWNLOAD;
            Merchant.BizContent = Util.SerializeObject((Auxiliary)auxiliary);
            GatewayData.Add(Merchant, StringCase.Snake);
            GatewayData.Add(Constant.SIGN, BuildSign());
        }

        #endregion

        #region 订单退款

        public INotify BuildRefund(IAuxiliary auxiliary)
        {
            InitRefund(auxiliary);

            Commit(Constant.ALIPAY_TRADE_REFUND_RESPONSE);

            return Notify;
        }

        public void InitRefund(IAuxiliary auxiliary)
        {
            InitAuxiliaryParameter(GatewayAuxiliaryType.Refund, auxiliary);
        }

        #endregion

        #region 查询退款订单

        public INotify BuildRefundQuery(IAuxiliary auxiliary)
        {
            InitRefundQuery(auxiliary);

            Commit(Constant.ALIPAY_TRADE_FASTPAY_REFUND_QUERY_RESPONSE);

            return Notify;
        }

        public void InitRefundQuery(IAuxiliary auxiliary)
        {
            InitAuxiliaryParameter(GatewayAuxiliaryType.RefundQuery, auxiliary);
        }

        #endregion

        protected override async Task<bool> ValidateNotifyAsync()
        {
            base.Notify = await GatewayData.ToObjectAsync<Notify>(StringCase.Snake);
            if (IsSuccessResult())
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 初始化订单参数
        /// </summary>
        private void InitOrderParameter()
        {
            Merchant.BizContent = Util.SerializeObject(Order);
            GatewayData.Add(Merchant, StringCase.Snake);
            GatewayData.Add(Constant.SIGN, BuildSign());
        }

        /// <summary>
        /// 初始化辅助接口的参数
        /// </summary>
        /// <param name="gatewayAuxiliaryType">辅助类型</param>
        /// <param name="auxiliary">辅助参数</param>
        private void InitAuxiliaryParameter(GatewayAuxiliaryType gatewayAuxiliaryType, IAuxiliary auxiliary)
        {
            auxiliary.Validate(gatewayAuxiliaryType);
            switch (gatewayAuxiliaryType)
            {
                case GatewayAuxiliaryType.Query:
                    Merchant.Method = Constant.QUERY;
                    break;
                case GatewayAuxiliaryType.Close:
                    Merchant.Method = Constant.CLOSE;
                    break;
                case GatewayAuxiliaryType.Cancel:
                    Merchant.Method = Constant.CANCEL;
                    break;
                case GatewayAuxiliaryType.Refund:
                    Merchant.Method = Constant.REFUND;
                    break;
                case GatewayAuxiliaryType.RefundQuery:
                    Merchant.Method = Constant.REFUNDQUERY;
                    break;
                default:
                    break;
            }
            Merchant.BizContent = Util.SerializeObject((Auxiliary)auxiliary);
            GatewayData.Add(Merchant, StringCase.Snake);
            GatewayData.Add(Constant.SIGN, BuildSign());
        }

        /// <summary>
        /// 提交请求
        /// </summary>
        /// <param name="type">结果类型</param>
        private void Commit(string type)
        {
            string result = null;
            Task.Run(async () =>
            {
                result = await HttpUtil
                 .PostAsync(RequestUrl, GatewayData.ToUrl());
            })
            .GetAwaiter()
            .GetResult();

            ReadReturnResult(result, type);
        }

        private string GetPaymentQueryString()
        {
            return GatewayData.ToUrl();
        }

        /// <summary>
        /// 读取返回结果
        /// </summary>
        /// <param name="result">结果</param>
        /// <param name="key">结果的对象名</param>
        private void ReadReturnResult(string result, string key)
        {
            GatewayData.FromJson(result);
            string sign = GatewayData.GetStringValue(Constant.SIGN);
            result = GatewayData.GetStringValue(key);
            GatewayData.FromJson(result);
            base.Notify = GatewayData.ToObject<Notify>(StringCase.Snake);
            Notify.Sign = sign;

            IsSuccessReturn();
        }

        /// <summary>
        /// 是否是已成功的返回
        /// </summary>
        /// <returns></returns>
        private bool IsSuccessReturn()
        {
            if (Notify.Code != "10000")
            {
                throw new GatewayException(Notify.SubMessage);
            }

            return true;
        }

        /// <summary>
        /// 生成签名
        /// </summary>
        private string BuildSign()
        {
            return EncryptUtil.RSA(GatewayData.ToUrl(false), Merchant.Privatekey, Merchant.SignType);
        }

        /// <summary>
        /// 是否是已成功支付的支付通知
        /// </summary>
        /// <returns></returns>
        private bool IsSuccessResult()
        {
            if (!ValidateNotifySign())
            {
                throw new GatewayException("签名不一致");
            }

            return true;
        }

        /// <summary>
        /// 验证支付宝通知的签名
        /// </summary>
        private bool ValidateNotifySign()
        {
            GatewayData.Remove(Constant.SIGN);
            GatewayData.Remove(Constant.SIGN_TYPE);

            return EncryptUtil.RSAVerifyData(GatewayData.ToUrl(false),
                Notify.Sign, Merchant.AlipayPublicKey, Merchant.SignType);
        }

        #endregion

        public override T Execute<T>(Request<T> request)
        {
            request.GatewayData.Add(Merchant, StringCase.Snake);
            request.GatewayData.Add(Constant.SIGN, BuildSign(request.GatewayData));

            string body = null;
            Task.Run(async () =>
            {
                body = await HttpUtil
                 .PostAsync(GatewayUrl + request.RequestUrl, request.GatewayData.ToUrl());
            })
            .GetAwaiter()
            .GetResult();

            GatewayData.FromJson(body);
            string sign = GatewayData.GetStringValue(Constant.SIGN);
            GatewayData.Remove(Constant.SIGN);
            GatewayData.FromJson(GatewayData[0].Value.ToString());
            GatewayData.Add(Constant.SIGN, sign);
            GatewayData.Add(BODY, body);

            return GatewayData.ToObject<T>(StringCase.Snake);
        }

        public override T SdkExecute<T>(Request<T> request)
        {
            request.RequestUrl = GatewayUrl + request.RequestUrl;
            request.GatewayData.Add(Merchant, StringCase.Snake);
            request.GatewayData.Add(Constant.SIGN, BuildSign(request.GatewayData));

            return (T)Activator.CreateInstance(typeof(T), request);
        }

        public string BuildSign(GatewayData gatewayData)
        {
            return EncryptUtil.RSA(gatewayData.ToUrl(false), Merchant.Privatekey, Merchant.SignType);
        }
    }
}