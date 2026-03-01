Feature: Studio acceptance criteria
  Validate JD.Writer v1 acceptance criteria across UI and API flows.

  Scenario: Command palette and slash suggestions are accessible
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I open the command palette with keyboard
    Then I should see the command palette
    When I close the command palette
    And I type "/summ" in the editor
    Then I should see slash command suggestions

  Scenario: Plugin slash template inserts structured markdown
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I type "/stand" in the editor
    Then I should see slash command suggestions
    When I select slash command "standup-template"
    Then the editor should contain "## Daily Standup"

  Scenario: Local-first persistence survives reload
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I set note title to "E2E Local Persistence"
    And I type "# Persisted Note\n\n- should survive reload" in the editor
    And I reload the page
    Then the note title input should be "E2E Local Persistence"
    And the editor should contain "# Persisted Note"

  Scenario: Corrupted local state is recovered without a circuit crash
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I inject corrupted local workspace state
    And I reload the page
    Then the studio title should contain "Markdown Studio"

  Scenario: Oversized local state does not break circuit initialization
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I inject oversized local workspace state
    And I reload the page
    Then the studio title should contain "Markdown Studio"

  Scenario: Preview render theme can change without app theme shift
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I choose preview render theme "terminal"
    Then preview render theme should be "terminal"
    And preview should use render class "preview-theme-terminal"
    When I reload the page
    Then preview render theme should be "terminal"
    And preview should use render class "preview-theme-terminal"

  Scenario: AI continue appends content in the editor
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I type "# Continue Target\n\n- first line" in the editor
    And I trigger AI continue from the toolbar
    Then editor content should be longer than before

  Scenario: Client-only mode continues draft without API service
    Given JD.Writer client-only mode is running for e2e tests
    When I open the studio home page
    And I type "# Standalone mode\n\n- keep writing locally" in the editor
    And I trigger AI continue from the toolbar
    Then editor content should be longer than before

  Scenario: Voice capture shortcut toggles and inserts transcript
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I enable voice test mode
    And I place cursor at the end of the editor
    And I toggle voice capture with keyboard
    And I inject voice transcript "voice dictated backlog item"
    Then the editor should contain "voice dictated backlog item"
    And voice status should contain "Voice:"

  Scenario: Voice interim transcript appears at cursor before final cleanup
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I enable voice test mode
    And I place cursor at the end of the editor
    And I toggle voice capture with keyboard
    When I inject interim voice transcript "words should flow instantly"
    Then the editor should contain "words should flow instantly"
    When I finalize voice transcript "words should flow instantly"
    Then local state should include voice transcript and cleanup operations

  Scenario: Voice capture toolbar toggle works
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I enable voice test mode
    And I toggle voice capture from the toolbar
    Then the voice toolbar button should contain "Mic (On)"
    When I toggle voice capture from the toolbar
    Then the voice toolbar button should contain "Mic (Off)"

  Scenario: Voice cleanup attempt is recorded in layer history
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I enable voice test mode
    And I place cursor at the end of the editor
    And I toggle voice capture with keyboard
    And I inject voice transcript "voice cleanup should run"
    Then local state should include voice transcript and cleanup operations

  Scenario: Voice recordings are reviewable in persisted audit logs
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I enable voice test mode
    And I place cursor at the end of the editor
    And I toggle voice capture with keyboard
    And I inject interim voice transcript "review log interim"
    And I finalize voice transcript "review log finalized payload"
    Then plugin panel "Voice Review" should be visible
    And local state should include voice session transcript events
    And voice review panel should contain "review log finalized payload"

  Scenario: System dark theme variables are applied
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I emulate dark color scheme
    Then app background variable should be "#070c16"

  Scenario: Edit layers are persisted with diff and tone metrics
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    And I type "# Layered Draft\n\n- checkpoint alpha" in the editor
    Then local state should contain note layers with diff and tone
    And plugin panel "History QC" should be visible

  Scenario: Plugin panels are loaded from manifest
    Given JD.Writer is running for e2e tests
    When I open the studio home page
    Then plugin panel "Risk Radar" should be visible
    And plugin panel "Open Questions" should be visible

  Scenario: API provider summary reports ollama preference
    Given JD.Writer is running for e2e tests
    When I request API provider summary
    Then provider preference should be "ollama"
    And ollama should be configured

  Scenario: AI API continue and slash endpoints return content
    Given JD.Writer is running for e2e tests
    When I request continuation for "# Plan\n\n- First idea"
    Then continuation output should be returned
    When I execute slash command "summarize" for "# Draft\n\nThis is a long paragraph to condense."
    Then slash command output should be returned

  Scenario: Assist stream endpoint returns NDJSON chunks
    Given JD.Writer is running for e2e tests
    When I request assist stream for mode "hints"
    Then assist stream should return at least 1 chunk

  Scenario: Plugin prompt stream endpoint returns NDJSON chunks
    Given JD.Writer is running for e2e tests
    When I request assist stream with custom prompt for mode "plugin-risks" and prompt "List concise delivery risks and mitigations."
    Then assist stream should return at least 1 chunk
