using Inventory.Application.Products;
using Inventory.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

/// <summary>Products and their stock levels.</summary>
[ApiController]
[Route("api/v1/products")]
[Authorize]
[Tags("Products")]
public sealed class ProductsController(IProductService productService) : ControllerBase
{
    private const string ProblemJson = "application/problem+json";

    /// <summary>Create a product.</summary>
    /// <remarks>The SKU must be unique (case-insensitive). New products start with nothing reserved or sold.</remarks>
    /// <response code="201">Created; `Location` points to the new product.</response>
    /// <response code="400">Missing SKU or name, or negative quantity (`VALIDATION_FAILED`).</response>
    /// <response code="409">A product with this SKU already exists (`DUPLICATE_SKU`).</response>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [Consumes("application/json")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status201Created, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)]
    public async Task<ActionResult<ProductResponse>> Create(CreateProductRequest request, CancellationToken cancellationToken)
    {
        var product = await productService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = product.Id }, product);
    }

    /// <summary>Get a product and its current stock.</summary>
    /// <param name="id">The product id.</param>
    /// <response code="200">The product, including `availableQuantity` = total − reserved − sold.</response>
    /// <response code="404">The product does not exist.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ProductResponse>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)]
    public async Task<ActionResult<ProductResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var product = await productService.GetByIdAsync(id, cancellationToken);
        return product is null ? NotFound() : Ok(product);
    }
}
