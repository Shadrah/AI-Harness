package domain

import "testing"

func TestNegotiateRejectsMissingVision(t *testing.T) {
	profile := ModelProfile{
		DisplayName: "text-only",
		Capabilities: map[CapabilityID]Capability{
			CapabilityTextInput: {ID: CapabilityTextInput, Support: SupportNative},
		},
	}
	issues := Negotiate(profile, []Requirement{{Capability: CapabilityImageInput}})
	if len(issues) != 1 || issues[0].Support != SupportUnknown {
		t.Fatalf("expected unknown vision issue, got %#v", issues)
	}
}

func TestNegotiateAcceptsNativeVision(t *testing.T) {
	profile := ModelProfile{
		DisplayName: "vision",
		Capabilities: map[CapabilityID]Capability{
			CapabilityImageInput: {ID: CapabilityImageInput, Support: SupportNative},
		},
	}
	if issues := Negotiate(profile, []Requirement{{Capability: CapabilityImageInput}}); len(issues) != 0 {
		t.Fatalf("expected vision negotiation to succeed, got %#v", issues)
	}
}

func TestValidateContentRejectsUnsupportedImageEncoding(t *testing.T) {
	profile := ModelProfile{
		DisplayName: "vision",
		Capabilities: map[CapabilityID]Capability{
			CapabilityImageInput: {
				ID:      CapabilityImageInput,
				Support: SupportNative,
				ImageConstraints: &ImageConstraints{
					MIMETypes:    []string{"image/png"},
					Sources:      []string{"path"},
					DetailLevels: []string{"auto", "high"},
				},
			},
		},
	}
	content := []ContentBlock{Image(Resource{
		Source:   SourceURL,
		Value:    "https://example.test/image.tiff",
		MIMEType: "image/tiff",
	}, ImageDetailOriginal)}
	issues := ValidateContent(profile, content)
	if len(issues) != 3 {
		t.Fatalf("expected source, MIME, and detail issues; got %#v", issues)
	}
}
