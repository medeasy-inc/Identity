﻿using Bogus;
using FluentAssertions;
using Identity.API.Features.Accounts;
using Identity.API.Routing;
using Identity.CQRS.Commands.Accounts;
using Identity.CQRS.Queries.Accounts;
using Identity.DataStores.SqlServer;
using Identity.DTO;
using Identity.Mapping;
using Identity.Objects;
using MedEasy.CQRS.Core.Commands;
using MedEasy.CQRS.Core.Commands.Results;
using MedEasy.CQRS.Core.Handlers;
using MedEasy.CQRS.Core.Queries;
using MedEasy.DAL.EFStore;
using MedEasy.DAL.Interfaces;
using MedEasy.DAL.Repositories;
using MedEasy.IntegrationTests.Core;
using MedEasy.RestObjects;
using MediatR;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Optional;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Xunit.Categories;
using static Microsoft.AspNetCore.Http.StatusCodes;
using static Moq.MockBehavior;
using static System.StringComparison;
using static MedEasy.RestObjects.LinkRelation;
using static System.Uri;

namespace Identity.API.Tests.Features.Accounts
{
    /// <summary>
    /// Unit tests for <see cref="AccountsController"/>
    /// </summary>
    [UnitTest]
    [Feature("Accounts")]
    [Feature("Identity")]
    public class AccountsControllerTests : IDisposable, IClassFixture<SqliteDatabaseFixture>
    {
        private ITestOutputHelper _outputHelper;

        private IUnitOfWorkFactory _uowFactory;
        private static readonly IdentityApiOptions _apiOptions = new IdentityApiOptions { DefaultPageSize = 30, MaxPageSize = 200 };
        private Mock<IMediator> _mediatorMock;
        private Mock<IUrlHelper> _urlHelperMock;
        private Mock<IOptionsSnapshot<IdentityApiOptions>> _apiOptionsMock;
        private AccountsController _sut;
        private const string _baseUrl = "http://host/api";

        public AccountsControllerTests(ITestOutputHelper outputHelper, SqliteDatabaseFixture database)
        {
            _outputHelper = outputHelper;

            _urlHelperMock = new Mock<IUrlHelper>(Strict);
            _urlHelperMock.Setup(mock => mock.Link(It.IsAny<string>(), It.IsAny<object>()))
                .Returns((string routename, object routeValues) => $"{_baseUrl}/{routename}/?{routeValues?.ToQueryString()}");

            _apiOptionsMock = new Mock<IOptionsSnapshot<IdentityApiOptions>>(Strict);

            DbContextOptionsBuilder<IdentityContext> dbContextOptionsBuilder = new DbContextOptionsBuilder<IdentityContext>();
            dbContextOptionsBuilder.UseSqlite(database.Connection)
                .EnableSensitiveDataLogging();

            _uowFactory = new EFUnitOfWorkFactory<IdentityContext>(dbContextOptionsBuilder.Options, (options) =>
            {
                IdentityContext context = new IdentityContext(options);
                context.Database.EnsureCreated();
                return context;
            });

            _mediatorMock = new Mock<IMediator>(Strict);

            _sut = new AccountsController(urlHelper: _urlHelperMock.Object, apiOptions: _apiOptionsMock.Object, mediator: _mediatorMock.Object);
        }

        public async void Dispose()
        {
            _outputHelper = null;
            using (IUnitOfWork uow = _uowFactory.NewUnitOfWork())
            {
                uow.Repository<Account>().Clear();
                await uow.SaveChangesAsync()
                    .ConfigureAwait(false);
            }
            _uowFactory = null;
            _urlHelperMock = null;
            _apiOptionsMock = null;
            _mediatorMock = null;
            _sut = null;
        }

        public static IEnumerable<object[]> GetAllTestCases
        {
            get
            {
                int[] pageSizes = { 1, 10, 500 };
                int[] pages = { 1, 10, 500 };

                foreach (int pageSize in pageSizes)
                {
                    foreach (int page in pages)
                    {
                        yield return new object[]
                        {
                            Enumerable.Empty<Account>(), // Current store state
                            pageSize, page, // request
                            0,    //expected total
                            (
                                firstPageUrlExpectation : (Expression<Func<Link, bool>>) (x => x != null && x.Relation == First  && $"{_baseUrl}/{RouteNames.DefaultGetAllApi}/?controller={AccountsController.EndpointName}&page=1&pageSize={Math.Min(pageSize, _apiOptions.MaxPageSize) }".Equals(x.Href, OrdinalIgnoreCase)), // expected link to first page
                                previousPageUrlExpectation : (Expression<Func<Link, bool>>) (x => x == null), // expected link to previous page
                                nextPageUrlExpectation : (Expression<Func<Link, bool>>) (x => x == null), // expected link to next page
                                lastPageUrlExpectation : (Expression<Func<Link, bool>>) (x => x != null && x.Relation == Last && $"{_baseUrl}/{RouteNames.DefaultGetAllApi}/?controller={AccountsController.EndpointName}&page=1&pageSize={Math.Min(pageSize, _apiOptions.MaxPageSize)}".Equals(x.Href, OrdinalIgnoreCase))  // expected link to last page
                            )
                        };
                    }
                }

                Faker<Account> accountFaker = new Faker<Account>()
                    .RuleFor(x => x.Id, 0)
                    .RuleFor(x => x.UUID, () => Guid.NewGuid())
                    .RuleFor(x => x.UserName, faker => faker.Internet.UserName())
                    .RuleFor(x => x.Email, faker => faker.Person.Email);

                {
                    IEnumerable<Account> items = accountFaker.Generate(400);
                    yield return new object[]
                    {
                        items,
                        PaginationConfiguration.DefaultPageSize, 1, // request
                        400,    //expected total
                        (
                            firstPageUrlExpecation : (Expression<Func<Link, bool>>) (x => x != null
                                && x.Relation == First
                                && $"{_baseUrl}/{RouteNames.DefaultGetAllApi}/?controller={AccountsController.EndpointName}&page=1&pageSize={PaginationConfiguration.DefaultPageSize}".Equals(x.Href, OrdinalIgnoreCase)), // expected link to first page
                            previousPageUrlExpecation : (Expression<Func<Link, bool>>) (x => x == null), // expected link to previous page
                            nextPageUrlExpecation : (Expression<Func<Link, bool>>) (x => x != null && x.Relation == "next" && $"{_baseUrl}/{RouteNames.DefaultGetAllApi}/?controller={AccountsController.EndpointName}&page=2&pageSize={PaginationConfiguration.DefaultPageSize}".Equals(x.Href, OrdinalIgnoreCase)), // expected link to next page
                            lastPageUrlExpecation : (Expression<Func<Link, bool>>) (x => x != null && x.Relation == Last && $"{_baseUrl}/{RouteNames.DefaultGetAllApi}/?controller={AccountsController.EndpointName}&page=14&pageSize={PaginationConfiguration.DefaultPageSize}".Equals(x.Href, OrdinalIgnoreCase))  // expected link to last page
                        )
                    };
                }
                {
                    IEnumerable<Account> items = accountFaker.Generate(400);

                    yield return new object[]
                    {
                        items,
                        10, 1, // request
                        400,    //expected total
                        (
                            firstPageUrlExpectation : (Expression<Func<Link, bool>>) (x => x != null && x.Relation == First  && $"{_baseUrl}/{RouteNames.DefaultGetAllApi}/?controller={AccountsController.EndpointName}&page=1&pageSize=10".Equals(x.Href, OrdinalIgnoreCase)), // expected link to first page
                            previousPageUrlExpectation : (Expression<Func<Link, bool>>) (x => x == null), // expected link to previous page
                            nextPageUrlExpectation : (Expression<Func<Link, bool>>) (x => x != null && x.Relation == "next" && $"{_baseUrl}/{RouteNames.DefaultGetAllApi}/?controller={AccountsController.EndpointName}&page=2&pageSize=10".Equals(x.Href, OrdinalIgnoreCase)), // expected link to next page
                            lastPageUrlExpectation : (Expression<Func<Link, bool>>) (x => x != null && x.Relation == Last && $"{_baseUrl}/{RouteNames.DefaultGetAllApi}/?controller={AccountsController.EndpointName}&page=40&pageSize=10".Equals(x.Href, OrdinalIgnoreCase))  // expected link to last page
                        )
                    };
                }
            }
        }

        [Theory]
        [MemberData(nameof(GetAllTestCases))]
        public async Task GetAll(IEnumerable<Account> items, int pageSize, int page,
            int expectedCount,
            (Expression<Func<Link, bool>> firstPageUrlExpectation, Expression<Func<Link, bool>> previousPageUrlExpectation, Expression<Func<Link, bool>> nextPageUrlExpectation, Expression<Func<Link, bool>> lastPageUrlExpectation) pageLinksExpectation)
        {
            _outputHelper.WriteLine($"Testing {nameof(AccountsController.Get)}({nameof(PaginationConfiguration)})");
            _outputHelper.WriteLine($"Page size : {pageSize}");
            _outputHelper.WriteLine($"Page : {page}");
            _outputHelper.WriteLine($"store items count: {items.Count()}");

            _apiOptionsMock.SetupGet(mock => mock.Value).Returns(_apiOptions);

            // Arrange
            _mediatorMock.Setup(mock => mock.Send(It.IsAny<GetPageOfAccountsQuery>(), It.IsAny<CancellationToken>()))
                .Returns((GetPageOfAccountsQuery query, CancellationToken cancellationToken) =>
                {
                    PaginationConfiguration pagination = query.Data;
                    Expression<Func<Account, AccountInfo>> expression = x => new AccountInfo { Id = x.UUID, Email = x.Email, Username = x.UserName };
                    Func<Account, AccountInfo> selector = expression.Compile();
                    _outputHelper.WriteLine($"Selector : {selector}");

                    IEnumerable<AccountInfo> results = items.Select(selector)
                        .ToArray();

                    results = results.Skip(pagination.PageSize * (pagination.Page == 1 ? 0 : pagination.Page - 1))
                         .Take(pagination.PageSize)
                         .ToArray();

                    return Task.FromResult(new Page<AccountInfo>(results, items.Count(), pagination.PageSize));
                });

            // Act
            IActionResult actionResult = await _sut.Get(new PaginationConfiguration { PageSize = pageSize, Page = page })
                .ConfigureAwait(false);

            // Assert
            _apiOptionsMock.Verify(mock => mock.Value, Times.Once);
            _mediatorMock.Verify(mock => mock.Send(It.IsAny<GetPageOfAccountsQuery>(), It.IsAny<CancellationToken>()), Times.Once);
            _mediatorMock.Verify(mock => mock.Send(It.Is<GetPageOfAccountsQuery>(cmd => cmd.Data.Page == page && cmd.Data.PageSize == Math.Min(pageSize, _apiOptions.MaxPageSize)), It.IsAny<CancellationToken>()), Times.Once,
                "Controller must cap pageSize of the query before sending it to the mediator");

            GenericPagedGetResponse<Browsable<AccountInfo>> response = actionResult.Should()
                    .NotBeNull().And
                    .BeOfType<OkObjectResult>().Which
                        .Value.Should()
                        .NotBeNull().And
                        .BeAssignableTo<GenericPagedGetResponse<Browsable<AccountInfo>>>().Which;

            response.Items.Should()
                .NotBeNull().And
                .NotContainNulls().And
                .NotContain(x => x.Resource == null).And
                .NotContain(x => x.Links == null);

            response.Total.Should()
                    .Be(expectedCount, $@"because the ""{nameof(GenericPagedGetResponse<Browsable<AccountInfo>>)}.{nameof(GenericPagedGetResponse<Browsable<AccountInfo>>.Total)}"" property indicates the number of elements");

            response.Links.First.Should().Match(pageLinksExpectation.firstPageUrlExpectation);
            response.Links.Previous.Should().Match(pageLinksExpectation.previousPageUrlExpectation);
            response.Links.Next.Should().Match(pageLinksExpectation.nextPageUrlExpectation);
            response.Links.Last.Should().Match(pageLinksExpectation.lastPageUrlExpectation);
        }

        [Fact]
        public async Task Delete_Returns_NoContent()
        {
            // Arrange
            _mediatorMock.Setup(mock => mock.Send(It.IsNotNull<DeleteAccountInfoByIdCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(DeleteCommandResult.Done);

            // Act
            Guid idToDelete = Guid.NewGuid();
            IActionResult actionResult = await _sut.Delete(idToDelete, ct: default)
                .ConfigureAwait(false);

            // Assert
            actionResult.Should()
                .BeAssignableTo<NoContentResult>();

            _mediatorMock.Verify(mock => mock.Send(It.IsNotNull<DeleteAccountInfoByIdCommand>(), It.IsAny<CancellationToken>()), Times.Once);
            _mediatorMock.Verify(mock => mock.Send(It.Is<DeleteAccountInfoByIdCommand>(cmd => cmd.Data == idToDelete), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Get_Returns_The_Element()
        {
            // Arrange
            Guid accountId = Guid.NewGuid();
            using (IUnitOfWork uow = _uowFactory.NewUnitOfWork())
            {
                uow.Repository<Account>().Create(new Account
                {
                    UUID = accountId,
                    UserName = "thebatman",
                    PasswordHash = "a_super_secret_password",
                    Email = "bruce@wayne-entreprise.com",
                    Salt = "salt_and_pepper_for_password"

                });

                await uow.SaveChangesAsync()
                    .ConfigureAwait(false);
            }

            _mediatorMock.Setup(mock => mock.Send(It.IsAny<GetOneAccountByIdQuery>(), It.IsAny<CancellationToken>()))
                .Returns(async (GetOneAccountByIdQuery query, CancellationToken ct) =>
                {
                    using (IUnitOfWork uow = _uowFactory.NewUnitOfWork())
                    {
                        Option<AccountInfo> result = await uow.Repository<Account>()
                            .SingleOrDefaultAsync(
                                x => new AccountInfo { Id = x.UUID, Email = x.Email, Username = x.UserName },
                                (Account x) => x.UUID == query.Data,
                                ct)
                            .ConfigureAwait(false);

                        return result;
                    }
                });

            // Act
            IActionResult actionResult = await _sut.Get(accountId, ct: default)
                .ConfigureAwait(false);

            // Assert
            _mediatorMock.Verify(mock => mock.Send(It.Is<GetOneAccountByIdQuery>(q => q.Data == accountId), It.IsAny<CancellationToken>()), Times.Once);

            Browsable<AccountInfo> browsableResource = actionResult.Should()
                .BeAssignableTo<OkObjectResult>().Which
                .Value.Should()
                .BeAssignableTo<Browsable<AccountInfo>>().Which;

            browsableResource.Links.Should()
                .NotBeNull().And
                .NotContainNulls().And
                .NotContain(x => string.IsNullOrWhiteSpace(x.Relation)).And
                .NotContain(x => string.IsNullOrWhiteSpace(x.Href)).And
                .ContainSingle(x => x.Relation == Self).And
                .ContainSingle(x => x.Relation == "delete");

            Link self = browsableResource.Links.Single(x => x.Relation == Self);
            self.Method.Should()
                .Be("GET");

            AccountInfo resource = browsableResource.Resource;
            self.Href.Should()
                .BeEquivalentTo($"{_baseUrl}/{RouteNames.DefaultGetOneByIdApi}/?controller={AccountsController.EndpointName}&{nameof(resource.Id)}={resource.Id}");

            Link delete = browsableResource.Links.Single(x => x.Relation == "delete");
            delete.Method.Should()
                .Be("DELETE");
            delete.Href.Should()
                .BeEquivalentTo($"{_baseUrl}/{RouteNames.DefaultGetOneByIdApi}/?controller={AccountsController.EndpointName}&{nameof(resource.Id)}={resource.Id}");

            resource.Id.Should().Be(accountId);
            resource.Username.Should().Be("thebatman");
            resource.Email.Should().Be("bruce@wayne-entreprise.com");
        }

        [Fact]
        public async Task Get_Returns_The_Element_With_Tenant()
        {
            // Arrange
            Guid accountId = Guid.NewGuid();

            Account tenant = new Account
            {
                UUID = Guid.NewGuid(),
                UserName = "thebatman",
                PasswordHash = "a_super_secret_password",
                Email = "bruce@wayne-entreprise.com",
                Salt = "salt_and_pepper_for_password",
                TenantId = Guid.NewGuid()
            };
            Account newAccount = new Account
            {
                UUID = accountId,
                UserName = "robin",
                PasswordHash = "a_super_secret_password",
                Email = "dick.grayson@wayne-entreprise.com",
                Salt = "salt_and_pepper_for_password",
                TenantId = tenant.UUID
            };

            using (IUnitOfWork uow = _uowFactory.NewUnitOfWork())
            {
                uow.Repository<Account>().Create(new[] { newAccount, tenant });

                await uow.SaveChangesAsync()
                    .ConfigureAwait(false);
            }

            _mediatorMock.Setup(mock => mock.Send(It.IsAny<GetOneAccountByIdQuery>(), It.IsAny<CancellationToken>()))
                .Returns(async (GetOneAccountByIdQuery query, CancellationToken ct) =>
                {
                    using (IUnitOfWork uow = _uowFactory.NewUnitOfWork())
                    {
                        Option<AccountInfo> result = await uow.Repository<Account>()
                            .SingleOrDefaultAsync(
                                x => new AccountInfo { Id = x.UUID, Email = x.Email, Username = x.UserName, TenantId = x.TenantId },
                                (Account x) => x.UUID == query.Data,
                                ct)
                            .ConfigureAwait(false);

                        return result;
                    }
                });

            // Act
            IActionResult actionResult = await _sut.Get(accountId, ct: default)
                .ConfigureAwait(false);

            // Assert
            _mediatorMock.Verify(mock => mock.Send(It.Is<GetOneAccountByIdQuery>(q => q.Data == accountId), It.IsAny<CancellationToken>()), Times.Once);

            Browsable<AccountInfo> browsableResource = actionResult.Should()
                .BeAssignableTo<OkObjectResult>().Which
                .Value.Should()
                .BeAssignableTo<Browsable<AccountInfo>>().Which;

            browsableResource.Links.Should()
                .NotBeNull().And
                .NotContainNulls().And
                .NotContain(x => string.IsNullOrWhiteSpace(x.Relation)).And
                .NotContain(x => string.IsNullOrWhiteSpace(x.Href)).And
                .ContainSingle(x => x.Relation == Self).And
                .ContainSingle(x => x.Relation == "tenant").And
                .ContainSingle(x => x.Relation == "delete");

            Link self = browsableResource.Links.Single(x => x.Relation == Self);
            self.Method.Should()
                .Be("GET");

            AccountInfo resource = browsableResource.Resource;
            self.Href.Should()
                .BeEquivalentTo($"{_baseUrl}/{RouteNames.DefaultGetOneByIdApi}/?controller={AccountsController.EndpointName}&{nameof(AccountInfo.Id)}={resource.Id}");

            Link tenantLink = browsableResource.Links.Single(x => x.Relation == "tenant");
            tenantLink.Href.Should()
                .BeEquivalentTo($"{_baseUrl}/{RouteNames.DefaultGetOneByIdApi}/?controller={AccountsController.EndpointName}&{nameof(AccountInfo.Id)}={resource.TenantId}");

            Link delete = browsableResource.Links.Single(x => x.Relation == "delete");
            delete.Method.Should()
                .Be("DELETE");
            delete.Href.Should()
                .BeEquivalentTo($"{_baseUrl}/{RouteNames.DefaultGetOneByIdApi}/?controller={AccountsController.EndpointName}&{nameof(resource.Id)}={resource.Id}");

            resource.Id.Should().Be(accountId);
            resource.Username.Should().Be(newAccount.UserName);
            resource.Email.Should().Be(newAccount.Email);
            resource.TenantId.Should().Be(newAccount.TenantId);
        }

        [Fact]
        public async Task Get_UnknonwnId_Returns_NotFound()
        {
            // Arrange
            _mediatorMock.Setup(mock => mock.Send(It.IsAny<GetOneAccountByIdQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Option.None<AccountInfo>());

            // Act
            IActionResult actionResult = await _sut.Get(id: Guid.NewGuid(), ct: default);

            // Assert
            actionResult.Should()
                .BeAssignableTo<NotFoundResult>();
        }

        [Fact]
        public async Task DeleteResource()
        {
            // Arrange
            _mediatorMock.Setup(mock => mock.Send(It.IsAny<DeleteAccountInfoByIdCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(DeleteCommandResult.Done);

            // Act
            Guid idToDelete = Guid.NewGuid();
            IActionResult actionResult = await _sut.Delete(idToDelete, ct: default)
                .ConfigureAwait(false);

            // Assert
            _mediatorMock.Verify(mock => mock.Send(It.IsAny<DeleteAccountInfoByIdCommand>(), It.IsAny<CancellationToken>()), Times.Once);
            _mediatorMock.Verify(mock => mock.Send(It.Is<DeleteAccountInfoByIdCommand>(cmd => cmd.Data == idToDelete), It.IsAny<CancellationToken>()), Times.Once);

            actionResult.Should()
                .BeAssignableTo<NoContentResult>();
        }

        [Fact]
        public async Task Delete_Unknown_Resource_Returns_Not_Found()
        {
            // Arrange
            _mediatorMock.Setup(mock => mock.Send(It.IsAny<DeleteAccountInfoByIdCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(DeleteCommandResult.Failed_NotFound);

            // Act
            Guid idToDelete = Guid.NewGuid();
            IActionResult actionResult = await _sut.Delete(idToDelete, ct: default)
                .ConfigureAwait(false);

            // Assert
            _mediatorMock.Verify(mock => mock.Send(It.IsAny<DeleteAccountInfoByIdCommand>(), It.IsAny<CancellationToken>()), Times.Once);
            _mediatorMock.Verify(mock => mock.Send(It.Is<DeleteAccountInfoByIdCommand>(cmd => cmd.Data == idToDelete), It.IsAny<CancellationToken>()), Times.Once);

            actionResult.Should()
                .BeAssignableTo<NotFoundResult>();
        }

        [Fact]
        public async Task Patch_UnknownEntity_Returns_NotFound()
        {
            JsonPatchDocument<AccountInfo> changes = new JsonPatchDocument<AccountInfo>();
            changes.Replace(x => x.Email, "bruce.wayne@gorham.com");

            _mediatorMock.Setup(mock => mock.Send(It.IsAny<PatchCommand<Guid, AccountInfo>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ModifyCommandResult.Failed_NotFound);

            // Act
            IActionResult actionResult = await _sut.Patch(id: Guid.NewGuid(), changes, ct: default)
                .ConfigureAwait(false);

            // Assert
            actionResult.Should()
                .BeAssignableTo<NotFoundResult>();
        }

        [Fact]
        public async Task Patch_Valid_Resource_Returns_NoContentResult()
        {
            // Arrange
            JsonPatchDocument<AccountInfo> changes = new JsonPatchDocument<AccountInfo>();
            changes.Replace(x => x.Email, "bruce.wayne@gorham.com");

            _mediatorMock.Setup(mock => mock.Send(It.IsAny<PatchCommand<Guid, AccountInfo>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ModifyCommandResult.Done);

            // Act
            IActionResult actionResult = await _sut.Patch(Guid.NewGuid(), changes)
                .ConfigureAwait(false);

            // Assert
            _mediatorMock.Verify(mock => mock.Send(It.IsAny<PatchCommand<Guid, AccountInfo>>(), It.IsAny<CancellationToken>()), Times.Once);

            actionResult.Should()
                .BeAssignableTo<NoContentResult>();
        }

        [Fact]
        public async Task GivenMediatorReturnsConflict_PostReturns_ConflictedResult()
        {
            // Arrange
            NewAccountInfo newAccount = new NewAccountInfo
            {
                Username = "thebatman",
                Email = "b.wayne@gotham.com"
            };

            _mediatorMock.Setup(mock => mock.Send(It.IsAny<CreateAccountInfoCommand>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Option.None<AccountInfo, CreateCommandResult>(CreateCommandResult.Failed_Conflict));

            // Act
            IActionResult actionResult = await _sut.Post(newAccount, ct: default)
                .ConfigureAwait(false);

            // Assert
            _mediatorMock.Verify(mock => mock.Send(It.IsAny<CreateAccountInfoCommand>(), It.IsAny<CancellationToken>()), Times.Once);
            _mediatorMock.Verify(mock => mock.Send(It.Is<CreateAccountInfoCommand>(cmd => cmd.Data == newAccount), It.IsAny<CancellationToken>()), Times.Once);

            actionResult.Should()
                .BeAssignableTo<StatusCodeResult>().Which
                .StatusCode.Should()
                    .Be(Status409Conflict);
        }

        [Fact]
        public async Task GivenMediatorReturnAccountCreated_PostReturns_OkObjectResult()
        {
            // Arrange
            NewAccountInfo newAccount = new NewAccountInfo
            {
                Username = "thebatman",
                Email = "b.wayne@gotham.com"
            };

            _mediatorMock.Setup(mock => mock.Send(It.IsAny<CreateAccountInfoCommand>(), It.IsAny<CancellationToken>()))
                .Returns((CreateAccountInfoCommand cmd, CancellationToken ct) =>
                    Task.FromResult(Option.Some<AccountInfo, CreateCommandResult>(new AccountInfo { Username = cmd.Data.Username, Id = Guid.NewGuid() })));

            // Act
            IActionResult actionResult = await _sut.Post(newAccount, ct: default)
                .ConfigureAwait(false);

            // Assert
            _mediatorMock.Verify(mock => mock.Send(It.IsAny<CreateAccountInfoCommand>(), It.IsAny<CancellationToken>()), Times.Once);
            _mediatorMock.Verify(mock => mock.Send(It.Is<CreateAccountInfoCommand>(cmd => cmd.Data == newAccount), It.IsAny<CancellationToken>()), Times.Once);

            CreatedAtRouteResult createdAtRouteResult = actionResult.Should()
                .BeAssignableTo<CreatedAtRouteResult>().Which;

            Browsable<AccountInfo> browsableResource = createdAtRouteResult.Value.Should()
                .BeAssignableTo<Browsable<AccountInfo>>().Which;

            AccountInfo createdResource = browsableResource.Resource;

            IEnumerable<Link> links = browsableResource.Links;
            links.Should()
                .NotBeNullOrEmpty().And
                .NotContainNulls().And
                .NotContain(link => string.IsNullOrWhiteSpace(link.Href)).And
                .NotContain(link => string.IsNullOrWhiteSpace(link.Method)).And
                .NotContain(link => string.IsNullOrWhiteSpace(link.Relation)).And
                .Contain(link => link.Relation == Self);

            Link linkSelf = links.Single(link => link.Relation == Self);
            linkSelf.Method.Should()
                .Be("GET");
            linkSelf.Href.Should()
                .Be($"{_baseUrl}/{RouteNames.DefaultGetOneByIdApi}/?controller={AccountsController.EndpointName}&{nameof(AccountInfo.Id)}={createdResource.Id}");

            createdResource.Username.Should()
                .Be(newAccount.Username);

            createdAtRouteResult.RouteName.Should()
                .Be(RouteNames.DefaultGetOneByIdApi);
            RouteValueDictionary routeValues = createdAtRouteResult.RouteValues;
            routeValues.Should()
                .ContainKey("controller").WhichValue.Should().Be(AccountsController.EndpointName);
            routeValues.Should()
                .ContainKey("id").WhichValue.Should()
                    .BeOfType<Guid>().Which.Should()
                    .NotBeEmpty();
        }

        public static IEnumerable<object[]> SearchTestCases
        {
            get
            {
                Faker<Account> accountFaker = new Faker<Account>()
                    .RuleFor(x => x.Id, 0)
                    .RuleFor(x => x.Name, faker => $"{faker.PickRandom("Bruce", "Clark", "Oliver", "Martha")} Wayne")
                    .RuleFor(x => x.Email, (faker, account) => faker.Internet.ExampleEmail(account.Name))
                    .RuleFor(x => x.PasswordHash, faker => faker.Lorem.Word())
                    .RuleFor(x => x.UserName, faker => faker.Internet.UserName())
                    .RuleFor(x => x.Salt, faker => faker.Lorem.Word())
                    .RuleFor(x => x.UUID, () => Guid.NewGuid())
                    .RuleFor(x => x.CreatedBy, faker => faker.Person.UserName)
                    .RuleFor(x => x.UpdatedBy, faker => faker.Person.UserName)
                    .RuleFor(x => x.CreatedDate, faker => faker.Date.Recent(days: 5))
                    .RuleFor(x => x.UpdatedDate, faker => faker.Date.Recent(days: 2))
                    ;
                {
                    IEnumerable<Account> items = accountFaker.Generate(40);

                    yield return new object[]
                    {
                        items,
                        new SearchAccountInfo
                        {
                            Name = "*Wayne",
                            Page = 1, PageSize = 10
                        },
                        (maxPageSize : 200, defaultPageSize : 30),
                        (
                            count : 40,
                            items :
                            (Expression<Func<IEnumerable<Browsable<SearchAccountInfoResult>>, bool>>)(resources =>
                                resources.All(x => x.Resource.Name.Like("*Wayne")))
                            ,
                            links :
                            (
                                firstPageUrlExpecation : (Expression<Func<Link, bool>>) (x => x != null
                                    && x.Relation == First
                                    && $"{_baseUrl}/{RouteNames.DefaultSearchResourcesApi}/?controller={AccountsController.EndpointName}&name={EscapeDataString("*Wayne")}&page=1&pageSize=10".Equals(x.Href, CurrentCultureIgnoreCase)), // expected link to first page
                                previousPageUrlExpecation : (Expression<Func<Link, bool>>) (x => x == null), // expected link to previous page
                                nextPageUrlExpecation : (Expression<Func<Link, bool>>) (x => x != null && x.Relation == Next && $"{_baseUrl}/{RouteNames.DefaultSearchResourcesApi}/?controller={AccountsController.EndpointName}&name={EscapeDataString("*Wayne")}&page=2&pageSize=10".Equals(x.Href, OrdinalIgnoreCase)), // expected link to next page
                                lastPageUrlExpecation : (Expression<Func<Link, bool>>) (x => x != null && x.Relation == Last && $"{_baseUrl}/{RouteNames.DefaultSearchResourcesApi}/?controller={AccountsController.EndpointName}&name={EscapeDataString("*Wayne")}&page=4&pageSize=10".Equals(x.Href, OrdinalIgnoreCase))  // expected link to last page
                            )
                        )
                    };
                }
            }
        }

        [Theory]
        [MemberData(nameof(SearchTestCases))]
        [Feature("Search")]
        public async Task Search(IEnumerable<Account> items, SearchAccountInfo searchQuery,
            (int maxPageSize, int defaultPageSize) apiOptions,
            (
                int count,
                Expression<Func<IEnumerable<Browsable<SearchAccountInfoResult>>, bool>> items,
                (
                    Expression<Func<Link, bool>> firstPageUrlExpectation,
                    Expression<Func<Link, bool>> previousPageUrlExpectation,
                    Expression<Func<Link, bool>> nextPageUrlExpectation,
                    Expression<Func<Link, bool>> lastPageUrlExpectation
                ) links
            ) pageExpectation)
        {
            _outputHelper.WriteLine($"Testing {nameof(AccountsController.Search)}({nameof(SearchAccountInfo)})");
            _outputHelper.WriteLine($"Search : {searchQuery.Stringify()}");
            _outputHelper.WriteLine($"store items: {items.Stringify()}");
            _outputHelper.WriteLine($"store items count: {items.Count()}");

            // Arrange
            using (IUnitOfWork uow = _uowFactory.NewUnitOfWork())
            {
                uow.Repository<Account>().Create(items);
                await uow.SaveChangesAsync()
                    .ConfigureAwait(false);
            }

            _apiOptionsMock.SetupGet(mock => mock.Value).Returns(new IdentityApiOptions { DefaultPageSize = apiOptions.defaultPageSize, MaxPageSize = apiOptions.maxPageSize });

            _mediatorMock.Setup(mock => mock.Send(It.IsAny<SearchQuery<SearchAccountInfoResult>>(), It.IsAny<CancellationToken>()))
                .Returns((SearchQuery<SearchAccountInfoResult> query, CancellationToken ct) =>
                {
                    return new HandleSearchQuery(_uowFactory, AutoMapperConfig.Build().ExpressionBuilder)
                        .Search<Account, SearchAccountInfoResult>(query, ct);
                });

            // Act
            IActionResult actionResult = await _sut.Search(searchQuery)
                .ConfigureAwait(false);

            // Assert
            _mediatorMock.Verify(mock => mock.Send(It.IsAny<SearchQuery<SearchAccountInfoResult>>(), It.IsAny<CancellationToken>()), Times.Once);
            _mediatorMock.Verify(mock => mock.Send(It.Is<SearchQuery<SearchAccountInfoResult>>(query => query.Data.Page == searchQuery.Page && query.Data.PageSize == Math.Min(searchQuery.PageSize, apiOptions.maxPageSize)), It.IsAny<CancellationToken>()), Times.Once);
            _apiOptionsMock.VerifyGet(mock => mock.Value, Times.AtLeastOnce, $"because {nameof(AccountsController)}.{nameof(AccountsController.Search)} must always check that " +
                $"{nameof(SearchAccountInfo.PageSize)} don't exceed {nameof(IdentityApiOptions.MaxPageSize)} value");

            GenericPagedGetResponse<Browsable<SearchAccountInfoResult>> response = actionResult.Should()
                    .NotBeNull().And
                    .BeOfType<OkObjectResult>().Which
                    .Value.Should()
                    .NotBeNull().And
                    .BeAssignableTo<GenericPagedGetResponse<Browsable<SearchAccountInfoResult>>>().Which;

            response.Items.Should()
                .NotBeNull().And
                .NotContainNulls().And
                .NotContain(x => x.Resource == null).And
                .NotContain(x => x.Links == null).And
                .NotContain(x => !x.Links.Any()).And
                .Match(pageExpectation.items);

            if (response.Items.Any())
            {
                response.Items.Should()
                    .OnlyContain(x => x.Links.Once(link => link.Relation == Self));
            }

            response.Total.Should()
                    .Be(pageExpectation.count, $@"the ""{nameof(GenericPagedGetResponse<Browsable<SearchAccountInfoResult>>)}.{nameof(GenericPagedGetResponse<Browsable<SearchAccountInfoResult>>.Total)}"" property indicates the number of elements");

            response.Links.First.Should().Match(pageExpectation.links.firstPageUrlExpectation);
            response.Links.Previous.Should().Match(pageExpectation.links.previousPageUrlExpectation);
            response.Links.Next.Should().Match(pageExpectation.links.nextPageUrlExpectation);
            response.Links.Last.Should().Match(pageExpectation.links.lastPageUrlExpectation);
        }
    }
}
