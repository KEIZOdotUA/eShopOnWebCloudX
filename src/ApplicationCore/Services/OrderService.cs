using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using Azure.Messaging.ServiceBus;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    private const string _orderQueueName = "ok-queue";

    public OrderService(IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    public async Task CreateOrderAsync(int basketId, Address shippingAddress)
    {
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.GetBySpecAsync(basketSpec);

        Guard.Against.NullBasket(basketId, basket);
        Guard.Against.EmptyBasketOnCheckout(basket.Items);

        var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
        var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

        var items = basket.Items.Select(basketItem =>
        {
            var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
            var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
            var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
            return orderItem;
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);

        await _orderRepository.AddAsync(order);

        await UploadToReserver(order);
        await UploadToProcessor(order);
    }

    private async Task UploadToReserver(Order order)
    {
        var client = new ServiceBusClient(_configuration["ConnectionStrings:AzureSB"]);
        var sender = client.CreateSender(_orderQueueName);
        using ServiceBusMessageBatch messageBatch = await sender.CreateMessageBatchAsync();
        messageBatch.TryAddMessage(new ServiceBusMessage(JsonConvert.SerializeObject(order)));

        try
        {
            await sender.SendMessagesAsync(messageBatch);
        }
        finally
        {
            await sender.DisposeAsync();
            await client.DisposeAsync();
        }
    }

    private async Task UploadToProcessor(Order order)
    {
        var client = _httpClientFactory.CreateClient("Azure Function");

        var request = new HttpRequestMessage(HttpMethod.Post, String.Empty)
        {
            Content = new StringContent(JsonConvert.SerializeObject(order), Encoding.UTF8, "application/json")
        };

        await client.SendAsync(request);
    }
}
