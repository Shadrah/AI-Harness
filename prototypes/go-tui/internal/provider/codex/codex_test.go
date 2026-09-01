package codex

import (
	"context"
	"testing"

	"github.com/will/harness/internal/domain"
)

func TestBootstrapProfileIncludesVision(t *testing.T) {
	profiles, err := New().Models(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if len(profiles) != 1 {
		t.Fatalf("expected one bootstrap profile, got %d", len(profiles))
	}
	vision := profiles[0].Capability(domain.CapabilityImageInput)
	if vision.Support != domain.SupportNative || vision.ImageConstraints == nil {
		t.Fatalf("vision contract missing: %#v", vision)
	}
	if len(vision.ImageConstraints.DetailLevels) == 0 {
		t.Fatal("vision detail levels missing")
	}
}

func TestDecodeNativeEventPreservesPayload(t *testing.T) {
	payload := []byte(`{"method":"item/updated","params":{"future":true}}`)
	event, err := DecodeNativeEvent(payload)
	if err != nil {
		t.Fatal(err)
	}
	if string(event.Native) != string(payload) {
		t.Fatalf("native payload changed: %s", event.Native)
	}
}
