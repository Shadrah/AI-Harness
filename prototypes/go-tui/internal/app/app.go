package app

import (
	"context"
	"fmt"
	"strings"

	tea "github.com/charmbracelet/bubbletea"
	"github.com/charmbracelet/lipgloss"
	"github.com/will/harness/internal/domain"
	"github.com/will/harness/internal/provider/codex"
)

type model struct {
	width      int
	height     int
	activePane int
	showHelp   bool
	showVision bool
	status     string
	profile    domain.ModelProfile
}

type runtimeMsg struct {
	status  string
	profile *domain.ModelProfile
}

var (
	borderColor = lipgloss.Color("63")
	accentColor = lipgloss.Color("212")
	dimColor    = lipgloss.Color("241")

	panelStyle = lipgloss.NewStyle().Border(lipgloss.RoundedBorder()).BorderForeground(borderColor).Padding(0, 1)
	titleStyle = lipgloss.NewStyle().Bold(true).Foreground(accentColor)
	dimStyle   = lipgloss.NewStyle().Foreground(dimColor)
)

func Run() error {
	profiles, _ := codex.New().Models(context.Background())
	m := model{status: "Detecting runtimes…"}
	if len(profiles) > 0 {
		m.profile = profiles[0]
	}
	_, err := tea.NewProgram(m, tea.WithAltScreen()).Run()
	return err
}

func (m model) Init() tea.Cmd {
	return func() tea.Msg {
		runtime := codex.New()
		status, err := runtime.Detect(context.Background())
		if err != nil {
			return runtimeMsg{status: "Codex detection error: " + err.Error()}
		}
		if !status.Available {
			return runtimeMsg{status: status.Message}
		}
		auth := "authentication needed"
		if status.Authenticated {
			auth = "authenticated"
		}
		message := runtimeMsg{status: fmt.Sprintf("%s · %s", status.Version, auth)}
		if profiles, modelErr := runtime.LiveModels(context.Background()); modelErr == nil && len(profiles) > 0 {
			message.profile = &profiles[0]
		}
		return message
	}
}

func (m model) Update(message tea.Msg) (tea.Model, tea.Cmd) {
	switch message := message.(type) {
	case tea.WindowSizeMsg:
		m.width, m.height = message.Width, message.Height
	case runtimeMsg:
		m.status = message.status
		if message.profile != nil {
			m.profile = *message.profile
		}
	case tea.KeyMsg:
		switch message.String() {
		case "q", "ctrl+c":
			return m, tea.Quit
		case "tab":
			m.activePane = (m.activePane + 1) % 4
		case "?":
			m.showHelp = !m.showHelp
		case "v":
			m.showVision = !m.showVision
		case "i":
			m.status = "Import preview: run `harness import <folder>`; interactive selection is next."
		case "a":
			m.status = "Image attachment encoding: run `harness codex-encode --image <path> <prompt>`."
		}
	}
	return m, nil
}

func (m model) View() string {
	if m.width == 0 {
		return "Starting Harness…"
	}
	header := titleStyle.Render("HARNESS") + "  " + dimStyle.Render("local-first agent workbench")
	footer := dimStyle.Render("tab panes · v vision · a attach · i import · ? help · q quit")
	if m.showHelp {
		footer = "Keys: tab change pane · v model vision · a attachment · i import hint · ? close help · q quit"
	}

	leftWidth := max(24, m.width/4)
	rightWidth := max(30, m.width-leftWidth-3)
	bodyHeight := max(8, m.height-5)
	topHeight := max(5, bodyHeight/2)
	bottomHeight := max(3, bodyHeight-topHeight-2)

	projects := m.panel(0, "Projects", "▸ Current directory\n  Imported projects\n  + Add project", leftWidth, topHeight)
	threads := m.panel(1, "Threads", "▸ Welcome\n  Import preview\n  Runtime doctor", leftWidth, bottomHeight)
	conversation := m.panel(2, "Task", m.taskContent(), rightWidth, topHeight)
	activity := m.panel(3, "Activity", "Runtime  "+m.status+"\nStorage  local\nPolicy   approvals required", rightWidth, bottomHeight)

	left := lipgloss.JoinVertical(lipgloss.Left, projects, threads)
	right := lipgloss.JoinVertical(lipgloss.Left, conversation, activity)
	body := lipgloss.JoinHorizontal(lipgloss.Top, left, right)
	return lipgloss.JoinVertical(lipgloss.Left, header, body, footer)
}

func (m model) panel(index int, title, content string, width, height int) string {
	style := panelStyle.Width(max(1, width-3)).Height(max(1, height-2))
	if m.activePane == index {
		style = style.BorderForeground(accentColor)
	}
	return style.Render(titleStyle.Render(title) + "\n" + content)
}

func (m model) taskContent() string {
	if m.showVision {
		capability := m.profile.Capability(domain.CapabilityImageInput)
		constraints := capability.ImageConstraints
		if constraints == nil {
			return "Vision: " + string(capability.Support)
		}
		return strings.Join([]string{
			"Model: " + m.profile.DisplayName,
			"Vision: " + string(capability.Support),
			"Sources: " + strings.Join(constraints.Sources, ", "),
			"MIME: " + strings.Join(constraints.MIMETypes, ", "),
			"Detail: " + strings.Join(constraints.DetailLevels, ", "),
			"",
			"Adapters must negotiate this contract before a run.",
		}, "\n")
	}
	return "Welcome. This shell will host projects, tasks, tool activity,\nimports, diffs, and model-native controls.\n\nPress v to inspect the selected model's vision contract."
}
