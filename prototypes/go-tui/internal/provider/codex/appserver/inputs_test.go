package appserver

import (
	"testing"

	"github.com/will/harness/internal/domain"
)

func TestUserInputsPreserveLocalImageDetail(t *testing.T) {
	profile := domain.ModelProfile{
		DisplayName: "vision model",
		Capabilities: map[domain.CapabilityID]domain.Capability{
			domain.CapabilityTextInput: {ID: domain.CapabilityTextInput, Support: domain.SupportNative},
			domain.CapabilityImageInput: {
				ID:      domain.CapabilityImageInput,
				Support: domain.SupportNative,
				ImageConstraints: &domain.ImageConstraints{
					Sources:      []string{"path", "url"},
					MIMETypes:    []string{"image/png"},
					DetailLevels: []string{"high", "original"},
				},
			},
		},
	}
	content := []domain.ContentBlock{
		domain.Text("inspect this"),
		domain.Image(domain.Resource{Source: domain.SourcePath, Value: `C:\art\sprite.png`, MIMEType: "image/png"}, domain.ImageDetailOriginal),
	}
	inputs, err := UserInputs(profile, content)
	if err != nil {
		t.Fatal(err)
	}
	if len(inputs) != 2 || inputs[1].Type != "localImage" || inputs[1].Detail != "original" {
		t.Fatalf("image input was not preserved: %#v", inputs)
	}
}
