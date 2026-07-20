using If.MicrosoftGraph;
using Microsoft.Graph.Models;
using Xunit;

namespace If.Tests;

public sealed class MailMapperTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static Message FullMessage(
        string id             = "AAMkAG001",
        string conversationId = "AAQkAG001",
        string subject        = "Renewal pricing discussion",
        string sender         = "daniel@northstarsoftware.com",
        string bodyPreview    = "We expect the renewal to reflect a 7% uplift.",
        DateTimeOffset? sentAt = null) => new Message
    {
        Id             = id,
        ConversationId = conversationId,
        Subject        = subject,
        From           = new Recipient { EmailAddress = new EmailAddress { Address = sender } },
        ToRecipients   =
        [
            new Recipient { EmailAddress = new EmailAddress { Address = "rishi@econtracts.onmicrosoft.com" } },
        ],
        CcRecipients   =
        [
            new Recipient { EmailAddress = new EmailAddress { Address = "legal@econtracts.onmicrosoft.com" } },
        ],
        BodyPreview    = bodyPreview,
        SentDateTime   = sentAt ?? new DateTimeOffset(2026, 6, 28, 14, 22, 0, TimeSpan.Zero),
    };

    // ── Full mapping ──────────────────────────────────────────────────────────

    [Fact]
    public void Map_FullyPopulatedMessage_MapsAllFieldsCorrectly()
    {
        var artifact = GraphMailMapper.Map(FullMessage(), "tenant-001", "upn@contoso.com");

        Assert.Equal("microsoft_graph",                    artifact.SourceSystem);
        Assert.Equal("mail_message",                       artifact.SourceType);
        Assert.Equal("tenant-001",                         artifact.TenantId);
        Assert.Equal("upn@contoso.com",                    artifact.SourcePrincipalId);
        Assert.Equal("daniel@northstarsoftware.com",       artifact.Sender);
        Assert.Equal("Renewal pricing discussion",         artifact.Subject);
        Assert.Equal("AAQkAG001",                          artifact.ConversationId);
        Assert.Equal(2,                                    artifact.Recipients.Count);
        Assert.Contains("rishi@econtracts.onmicrosoft.com", artifact.Recipients);
        Assert.Contains("legal@econtracts.onmicrosoft.com", artifact.Recipients);
        Assert.NotEqual(Guid.Empty,                        artifact.ArtifactId);
        Assert.Equal(new DateTimeOffset(2026, 6, 28, 14, 22, 0, TimeSpan.Zero), artifact.SentAtUtc);
    }

    // ── ExternalId format ─────────────────────────────────────────────────────

    [Fact]
    public void Map_ExternalId_HasMsgraphMessagePrefix()
    {
        var artifact = GraphMailMapper.Map(FullMessage(id: "AAMkAG999"), "t", "u");
        Assert.StartsWith("msgraph:message:", artifact.ExternalId);
        Assert.Equal("msgraph:message:AAMkAG999", artifact.ExternalId);
    }

    [Fact]
    public void Map_ExternalId_NullMessageId_ProducesEmptySuffix()
    {
        var msg = FullMessage();
        msg.Id  = null;
        var artifact = GraphMailMapper.Map(msg, "t", "u");
        Assert.Equal("msgraph:message:", artifact.ExternalId);
    }

    // ── Null sender ───────────────────────────────────────────────────────────

    [Fact]
    public void Map_NullFrom_DoesNotThrow_ReturnsEmptyString()
    {
        var msg = FullMessage();
        msg.From = null;
        var artifact = GraphMailMapper.Map(msg, "t", "u");
        Assert.Equal("", artifact.Sender);
    }

    [Fact]
    public void Map_FromWithNullEmailAddress_DoesNotThrow()
    {
        var msg = FullMessage();
        msg.From = new Recipient { EmailAddress = null };
        var artifact = GraphMailMapper.Map(msg, "t", "u");
        Assert.Equal("", artifact.Sender);
    }

    // ── Recipients (To + Cc combined) ─────────────────────────────────────────

    [Fact]
    public void Map_NullToAndCc_ReturnsEmptyRecipients()
    {
        var msg = FullMessage();
        msg.ToRecipients = null;
        msg.CcRecipients = null;
        var artifact = GraphMailMapper.Map(msg, "t", "u");
        Assert.NotNull(artifact.Recipients);
        Assert.Empty(artifact.Recipients);
    }

    [Fact]
    public void Map_EmptyToAndCc_ReturnsEmptyRecipients()
    {
        var msg = FullMessage();
        msg.ToRecipients = [];
        msg.CcRecipients = [];
        var artifact = GraphMailMapper.Map(msg, "t", "u");
        Assert.Empty(artifact.Recipients);
    }

    [Fact]
    public void Map_Recipients_CombinesToAndCc_ExcludesNulls()
    {
        var msg = FullMessage();
        msg.ToRecipients =
        [
            new Recipient { EmailAddress = new EmailAddress { Address = "to@a.com" } },
            new Recipient { EmailAddress = null },
        ];
        msg.CcRecipients =
        [
            new Recipient { EmailAddress = new EmailAddress { Address = "cc@b.com" } },
            new Recipient { EmailAddress = new EmailAddress { Address = null } },
        ];
        var artifact = GraphMailMapper.Map(msg, "t", "u");
        Assert.Equal(2, artifact.Recipients.Count);
        Assert.Contains("to@a.com", artifact.Recipients);
        Assert.Contains("cc@b.com", artifact.Recipients);
    }

    [Fact]
    public void Map_OnlyCc_NullTo_StillMapped()
    {
        var msg = FullMessage();
        msg.ToRecipients = null;
        msg.CcRecipients =
        [
            new Recipient { EmailAddress = new EmailAddress { Address = "cc@contoso.com" } },
        ];
        var artifact = GraphMailMapper.Map(msg, "t", "u");
        var addr = Assert.Single(artifact.Recipients);
        Assert.Equal("cc@contoso.com", addr);
    }

    // ── BodyPreview truncation ────────────────────────────────────────────────

    [Fact]
    public void Map_NullBodyPreview_ReturnsEmptyString()
    {
        var msg = FullMessage();
        msg.BodyPreview = null;
        var artifact = GraphMailMapper.Map(msg, "t", "u");
        Assert.Equal("", artifact.BodyPreview);
    }

    [Fact]
    public void Map_BodyPreview_TruncatedAt500Chars()
    {
        var msg = FullMessage();
        msg.BodyPreview = new string('x', 600);
        var artifact = GraphMailMapper.Map(msg, "t", "u");
        Assert.Equal(500, artifact.BodyPreview.Length);
    }

    [Fact]
    public void Map_BodyPreview_ShortBody_NotTruncated()
    {
        var msg = FullMessage(bodyPreview: "Short body.");
        var artifact = GraphMailMapper.Map(msg, "t", "u");
        Assert.Equal("Short body.", artifact.BodyPreview);
    }

    // ── SentAtUtc ─────────────────────────────────────────────────────────────

    [Fact]
    public void Map_SentDateTime_PreservedAsIs()
    {
        var sent = new DateTimeOffset(2026, 3, 15, 9, 30, 0, TimeSpan.Zero);
        var artifact = GraphMailMapper.Map(FullMessage(sentAt: sent), "t", "u");
        Assert.Equal(sent, artifact.SentAtUtc);
    }

    [Fact]
    public void Map_NullSentDateTime_DoesNotThrow()
    {
        var msg = FullMessage();
        msg.SentDateTime = null;
        var artifact = GraphMailMapper.Map(msg, "t", "u");
        Assert.True(artifact.SentAtUtc > DateTimeOffset.MinValue);
    }
}
