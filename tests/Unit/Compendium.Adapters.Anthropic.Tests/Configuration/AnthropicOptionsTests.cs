// -----------------------------------------------------------------------
// <copyright file="AnthropicOptionsTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.Anthropic.Tests.Configuration;

public class AnthropicOptionsTests
{
    [Fact]
    public void AnthropicOptions_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var options = new AnthropicOptions();

        // Assert
        options.ApiKey.Should().BeEmpty();
        options.BaseUrl.Should().Be("https://api.anthropic.com");
        options.AnthropicVersion.Should().Be("2023-06-01");
        options.DefaultModel.Should().Be("claude-3-7-sonnet-latest");
        options.DefaultMaxTokens.Should().Be(4096);
        options.TimeoutSeconds.Should().Be(120);
        options.EnableLogging.Should().BeFalse();
        options.EnablePromptCaching.Should().BeFalse();
        options.WorkspaceId.Should().BeNull();
    }

    [Fact]
    public void SectionName_IsAnthropic()
    {
        AnthropicOptions.SectionName.Should().Be("Anthropic");
    }

    [Fact]
    public void DefaultConstants_ExposeBaseUrlAndVersion()
    {
        AnthropicOptions.DefaultBaseUrl.Should().Be("https://api.anthropic.com");
        AnthropicOptions.DefaultAnthropicVersion.Should().Be("2023-06-01");
    }

    [Fact]
    public void IsValid_WithFullyPopulatedOptions_ReturnsTrue()
    {
        // Arrange
        var options = new AnthropicOptions { ApiKey = "sk-ant-x" };

        // Act & Assert
        options.IsValid().Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsValid_WithMissingApiKey_ReturnsFalse(string? apiKey)
    {
        // Arrange
        var options = new AnthropicOptions { ApiKey = apiKey! };

        // Act & Assert
        options.IsValid().Should().BeFalse();
    }

    [Fact]
    public void IsValid_WithMissingBaseUrl_ReturnsFalse()
    {
        var options = new AnthropicOptions { ApiKey = "sk", BaseUrl = string.Empty };
        options.IsValid().Should().BeFalse();
    }

    [Fact]
    public void IsValid_WithMissingAnthropicVersion_ReturnsFalse()
    {
        var options = new AnthropicOptions { ApiKey = "sk", AnthropicVersion = string.Empty };
        options.IsValid().Should().BeFalse();
    }

    [Fact]
    public void IsValid_WithMissingDefaultModel_ReturnsFalse()
    {
        var options = new AnthropicOptions { ApiKey = "sk", DefaultModel = string.Empty };
        options.IsValid().Should().BeFalse();
    }

    [Fact]
    public void IsValid_WithNonPositiveDefaultMaxTokens_ReturnsFalse()
    {
        var options = new AnthropicOptions { ApiKey = "sk", DefaultMaxTokens = 0 };
        options.IsValid().Should().BeFalse();
    }

    [Fact]
    public void IsValid_WithNonPositiveTimeoutSeconds_ReturnsFalse()
    {
        var options = new AnthropicOptions { ApiKey = "sk", TimeoutSeconds = 0 };
        options.IsValid().Should().BeFalse();
    }

    [Fact]
    public void AnthropicOptions_WithCustomValues_RetainsValues()
    {
        // Arrange & Act
        var options = new AnthropicOptions
        {
            ApiKey = "sk-ant-real",
            BaseUrl = "https://custom.anthropic.example",
            AnthropicVersion = "2026-01-01",
            DefaultModel = "claude-opus-4-5",
            DefaultMaxTokens = 8192,
            TimeoutSeconds = 60,
            EnableLogging = true,
            EnablePromptCaching = true,
        };

        // Assert
        options.ApiKey.Should().Be("sk-ant-real");
        options.BaseUrl.Should().Be("https://custom.anthropic.example");
        options.AnthropicVersion.Should().Be("2026-01-01");
        options.DefaultModel.Should().Be("claude-opus-4-5");
        options.DefaultMaxTokens.Should().Be(8192);
        options.TimeoutSeconds.Should().Be(60);
        options.EnableLogging.Should().BeTrue();
        options.EnablePromptCaching.Should().BeTrue();
    }
}
