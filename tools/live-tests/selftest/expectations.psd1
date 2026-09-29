<#
    What each shipped live test must record when it is run against the fake machine.

    The key is "<test id>|<half>". Under it, one entry per set of fake inputs:

      none  every log pattern and every evidence list is empty
      one   exactly one matching line and one item in every list
      two   two of each

    Criteria holds every criterion the run must record, with the outcome the fake inputs
    imply. A criterion that is recorded and is not named here is a failure too, so a branch
    that stops being reached cannot go unnoticed.

    Findings is optional, but once it is declared for a case it is exhaustive, the same as
    Criteria: every finding that case's run records must be named, and every one named must be
    recorded. A value of $null means the finding must be recorded as not measured (Add-Finding's
    own $null convention); anything else must match exactly. Only test 13's block declares it,
    because it is the one row whose run records nothing this file does not already want pinned.

    FindingsInclude is the one that is never exhaustive: it names one or two findings worth
    pinning down on a case whose run also records others this file has no reason to enumerate,
    such as any of the at-rest closing step's cases sitting on a script with findings of its own.
    A finding named here that the run does not record is still a failure; anything the run
    records that is not named here is not checked at all.

    Steps names, for a case worth it, how many times a given command line actually ran (an
    offered step that was declined or that failed to start does not count); like FindingsInclude
    it is never exhaustive. SummaryContains names phrases summary.txt must hold, for proving text
    such as the at-rest warning reaches the file a person reads, not only result.json.
    ExpectedErrors, when a case's run is meant to record an error on purpose, is how many; every
    other case is checked against zero.

    Three rules were used to work these out, and only these:

      * the fake machine starts in the state that test's own preconditions describe, and each
        command moves it the way the real one would (Fakes.psm1, StartStates and
        Update-FakeWorld),
      * the owner answers the way a machine in good order would be answered (Fakes.psm1,
        Answers), and
      * anything counted out of the log or out of an evidence list holds 0, 1 or 2 items
        depending on the case. That is the only difference between the three columns, so a
        criterion whose outcome changes between them is one the counting decides, and those
        are the ones the unrolling defect used to destroy.

    "any" means the criterion must be recorded but its outcome is not this machine's to
    decide. There are three, and all three read something outside the sandbox that cannot be
    redirected: the startup value under the user's own Run key, and the program folder, which
    Windows resets to the real Program Files in every child process whatever the parent set
    ProgramFiles to. Overall may then be one of two outcomes, which is what the list is for.
#>
@{

    # ---------------------------------------------------------------- 00 restore
    # Nothing here is counted out of the log or an evidence list, so all three columns agree.
    # The at-rest default table (Invoke-SelfTest.ps1) pins leftAtRest = yes and one "diag gate
    # block" for none/one/two: the restore steps allow the nodes on purpose, and the closing
    # check's own offer is what blocks them again.
    #
    # atrest-guard-throws forces something to throw deep inside the closing step (see
    # Run-OneHalf.ps1's Invoke-Earshot, label 'at-rest-nodes'), proving Close-AtRest's own catch
    # and Complete-LiveTestRun's wrapping one: the run still writes result.json, records the one
    # error the catch adds, and leftAtRest is recorded as unknown rather than guessed.
    # 00-Restore's own criteria are untouched, because the label this case intercepts belongs
    # only to Close-AtRest. The real null-handling paths (a failed probe that returns $null
    # rather than throwing) are proved on row 01 instead, where they can be told apart from this.
    '00-restore|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'nodes' = 'pass'; 'protection' = 'pass'; 'block-at-boot' = 'pass'; 'tasks-as-shipped' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'nodes' = 'pass'; 'protection' = 'pass'; 'block-at-boot' = 'pass'; 'tasks-as-shipped' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'nodes' = 'pass'; 'protection' = 'pass'; 'block-at-boot' = 'pass'; 'tasks-as-shipped' = 'pass' } }
        'atrest-guard-throws' = @{ Overall = 'pass'; Criteria = @{ 'nodes' = 'pass'; 'protection' = 'pass'; 'block-at-boot' = 'pass'; 'tasks-as-shipped' = 'pass' }
            Findings = @{ 'leftAtRest' = 'unknown' }
            ExpectedErrors = 1 }
        # The regression case for the closing step's own disconnect-first fix: the allow pages the
        # AirPods (Fakes.psm1's own case-restricted rule, Update-FakeWorld), so the closing step
        # reads render ACTIVE, disconnects first, confirms, and only then blocks. On the old step, with
        # no disconnect, this is exactly where the block is vetoed (Get-FakeGateEvidence's own
        # world rule): leftAtRest no, blockStep.gateResult partial, "diag disconnect" ran 0 times.
        'atrest-render-active' = @{ Overall = 'pass'; Criteria = @{ 'nodes' = 'pass'; 'protection' = 'pass'; 'block-at-boot' = 'pass'; 'tasks-as-shipped' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'yes' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 1 } }
    }

    # ------------------------------------------------------------ 01 A2DP one-shot
    # The filters a ks run finds depend on protection, not on the case, and the notification
    # count this test reads is only a finding, so all three columns agree, and every atrest-*
    # case below carries the SAME Criteria as none, because every label these cases intercept
    # (Run-OneHalf.ps1's Invoke-Earshot: 'at-rest-task', 'at-rest-nodes', 'at-rest-nodes-after',
    # 'at-rest-block') belongs only to Close-AtRest, which runs after 01's own criteria are
    # already recorded.
    #
    # 01 never sends "diag gate block" itself, and every case ends with the nodes Allowed, so
    # this is where the at-rest closing step's own offer is the only "diag gate block" step in
    # the run. none/one/two are pinned by the at-rest default table (Invoke-SelfTest.ps1) rather
    # than repeated here: leftAtRest = yes, one block sent, proving the owner accepts and exactly
    # one block is sent (case a). The named cases below are this row's own, each intercepting a
    # different Close-AtRest label to prove one real failure shape.
    '01-a2dp-oneshot|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' } }
        # The owner declines the one offered block: case (b). No block is sent, and the warning
        # (both the headline and the consequence and remedy lines the offer and the warning carry)
        # reaches summary.txt, not only the printed result block.
        'atrest-decline' = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'no' }
            Steps = @{ 'diag gate block' = 0; 'diag disconnect' = 1 }
            SummaryContains = @(
                'THE MACHINE IS NOT AT REST.'
                'Blocks the AirPods Bluetooth nodes so this PC does not page them at the next boot.'
                'start the Earshot tray, whose own start-up check blocks the nodes when they are not'
            ) }
        # The block step answers a plain success (exit 0, the gate's own evidence says Success
        # too), but Fakes.psm1's Update-FakeWorld leaves the nodes exactly where they were: only
        # the closing check's re-read can catch that, never the step's own reported result. This
        # is the mutation the exit-code shortcut would pass and the re-read does not.
        'atrest-block-ineffective' = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'no' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 1 }
            SummaryContains = @('THE MACHINE IS NOT AT REST.')
            ExpectedErrors = 0 }
        # The task probe that answers setUp fails the real way (an error recorded, $null
        # returned, never a throw). Block at boot still reads true, so this is not a positive
        # "not set up", and the check must still read the nodes, offer and succeed: yes, not
        # not-applicable and not a guess.
        'atrest-setup-unknown' = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'yes' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 1 }
            ExpectedErrors = 1 }
        # config.json itself is missing (New-FakeSandbox removes it for this case), so
        # Get-BlockAtBootSetting answers $null without throwing or recording an error, the real
        # shape a missing file takes. setUp still reads true, so again this must read the nodes
        # rather than call it not-applicable.
        'atrest-config-missing' = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'yes' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 1 }
            ExpectedErrors = 0 }
        # The node probe fails once, before the offer, so there is nothing positive to say yet;
        # the offer still happens ("still offer and warn"), and the re-read after it succeeds, so
        # the honest final answer is yes, not a hedge, because a real reading was eventually made.
        'atrest-nodes-probe-fails' = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'yes' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 1 }
            ExpectedErrors = 1 }
        # Neither the read before the offer nor the one after it ever answers, even though the
        # offer itself still runs (and, in the fake world, actually blocks the nodes): the script
        # never gets to observe that, so the honest record is unknown, never not-applicable and
        # never a guessed no. The warning still fires, because nothing here confirmed Blocked.
        'atrest-nodes-stay-unreadable' = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'unknown' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 1 }
            SummaryContains = @('THE MACHINE IS NOT AT REST.')
            ExpectedErrors = 2 }
        # The disconnect offer is declined: the block is still offered (nothing that reaches a
        # block today stops reaching it), with the while-playing consequence, and is vetoed
        # because render is still ACTIVE (Get-FakeGateEvidence's own world rule). Both new warning
        # lines, and the headline, must reach summary.txt, not only result.json.
        'atrest-disconnect-declined' = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'no' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 0 }
            SummaryContains = @(
                'THE MACHINE IS NOT AT REST.'
                'You declined to disconnect them first, so the AirPods may still be playing from this PC, and Windows refuses to disable their audio entry while they are (CR_REMOVE_VETOED on 21 September 2026).'
                'Stop them playing from this PC (put them in their case, or left-click the Earshot icon, which disconnects and then blocks), then run 00-Restore.ps1 and accept its closing offers.'
            )
            ExpectedErrors = 0 }
        # The disconnect verb runs but never reaches the wanted state (Get-FakeCommandAnswer's own
        # case-keyed branch), so render still reads ACTIVE on the harness's own re-read: confirmed
        # is false, the block is offered with the while-playing consequence, and is vetoed.
        'atrest-disconnect-not-confirmed' = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'no' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 1 }
            SummaryContains = @(
                'THE MACHINE IS NOT AT REST.'
                'The AirPods were still playing from this PC when the block ran, and Windows refuses to disable their audio entry while they are (CR_REMOVE_VETOED on 21 September 2026).'
            )
            ExpectedErrors = 0 }
        # The pre-offer render read fails outright (the same failed-probe shape as the node reads
        # above), so renderBefore is not recorded, not a positive not-ACTIVE reading: the
        # disconnect is offered anyway (fail closed). It runs for real and confirms, so the block
        # that follows uses the ordinary consequence and is not vetoed.
        'atrest-audio-unreadable' = @{ Overall = 'pass'; Criteria = @{
                'topology-reachable' = 'pass'; 'preconditions' = 'pass'; 'protection-on' = 'pass'
                'K1-src-protected' = 'pass'; 'K1-src-active' = 'pass'; 'K1-budget' = 'pass'
                'K1-wave-absent' = 'pass'; 'K1-all-protected' = 'pass'; 'K1-src-unprotected' = 'pass'
                'K1-wave-brings-a2dp' = 'pass'; 'restored' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'yes' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 1 }
            ExpectedErrors = 1 }
    }

    # -------------------------------------------------------------- 02 disconnect
    '02-disconnect|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{
                'preconditions' = 'pass'; 'disconnect-src' = 'pass'; 'disconnect-src-unplugged' = 'pass'
                'disconnect-budget' = 'pass'; 'disconnect-wave' = 'pass'; 'disconnect-all' = 'pass'
                'block-recorded' = 'pass'; 'left-allowed' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'preconditions' = 'pass'; 'disconnect-src' = 'pass'; 'disconnect-src-unplugged' = 'pass'
                'disconnect-budget' = 'pass'; 'disconnect-wave' = 'pass'; 'disconnect-all' = 'pass'
                'block-recorded' = 'pass'; 'left-allowed' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{
                'preconditions' = 'pass'; 'disconnect-src' = 'pass'; 'disconnect-src-unplugged' = 'pass'
                'disconnect-budget' = 'pass'; 'disconnect-wave' = 'pass'; 'disconnect-all' = 'pass'
                'block-recorded' = 'pass'; 'left-allowed' = 'pass' } }
    }

    # ------------------------------------------------------------- 03 allow pages
    '03-allow-pages|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'preconditions' = 'pass'; 'allow' = 'pass'; 'no-auto-page' = 'pass'; 'left-blocked' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'preconditions' = 'pass'; 'allow' = 'pass'; 'no-auto-page' = 'pass'; 'left-blocked' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'preconditions' = 'pass'; 'allow' = 'pass'; 'no-auto-page' = 'pass'; 'left-blocked' = 'pass' } }
    }

    # --------------------------------------------------------- 04 block and reboot
    # nodes-present is not recorded: the fake device has target nodes, so that branch is not
    # the one taken.
    '04-block-and-reboot|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'block' = 'pass'; 'disabled-now' = 'pass'; 'locatable' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'block' = 'pass'; 'disabled-now' = 'pass'; 'locatable' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'block' = 'pass'; 'disabled-now' = 'pass'; 'locatable' = 'pass' } }
    }
    '04-block-and-reboot|resume' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'persisted' = 'pass'; 'stayed-on-phone' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'persisted' = 'pass'; 'stayed-on-phone' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'persisted' = 'pass'; 'stayed-on-phone' = 'pass' } }
    }

    # ------------------------------------------------------------------- 05 allow
    '05-allow|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'allow' = 'pass'; 'problem-cleared' = 'pass'; 'bit-cleared' = 'pass'; 'endpoints-back' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'allow' = 'pass'; 'problem-cleared' = 'pass'; 'bit-cleared' = 'pass'; 'endpoints-back' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'allow' = 'pass'; 'problem-cleared' = 'pass'; 'bit-cleared' = 'pass'; 'endpoints-back' = 'pass' } }
    }
    '05-allow|resume' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'still-allowed' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'still-allowed' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'still-allowed' = 'pass' } }
    }

    # --------------------------------------------------------------- 06 Handsfree
    # Two criteria here are counted, and they are the reason this test is in the self-test:
    # unelevated-recorded counts the steps in the diag evidence, and
    # services-readable-while-blocked counts the services the probe listed while the nodes
    # were disabled. With nothing to count, the first is inconclusive and the second fails,
    # which is the honest answer and not a throw.
    '06-handsfree|first' = @{
        none = @{ Overall = 'fail'; Criteria = @{
                'unelevated-recorded' = 'inconclusive'; 'gate-protect-on' = 'pass'; 'within-task-limit' = 'pass'
                'protection-survives-reconnect' = 'pass'; 'services-readable-while-blocked' = 'fail'; 'left-as-asked' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'unelevated-recorded' = 'pass'; 'gate-protect-on' = 'pass'; 'within-task-limit' = 'pass'
                'protection-survives-reconnect' = 'pass'; 'services-readable-while-blocked' = 'pass'; 'left-as-asked' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{
                'unelevated-recorded' = 'pass'; 'gate-protect-on' = 'pass'; 'within-task-limit' = 'pass'
                'protection-survives-reconnect' = 'pass'; 'services-readable-while-blocked' = 'pass'; 'left-as-asked' = 'pass' } }
    }

    # --------------------------------------------------------------- 07 task RunEx
    # planb is not recorded: the fake tasks may be started from this token and the gate run
    # completes, so plan B is not reached.
    '07-task-runex|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{
                'setup' = 'pass'; 'ace-present' = 'pass'; 'runex' = 'pass'; 'arguments-arrive' = 'pass'
                'lasttaskresult' = 'pass'; 'setboot-round-trip' = 'pass'; 'setboot-restored' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'setup' = 'pass'; 'ace-present' = 'pass'; 'runex' = 'pass'; 'arguments-arrive' = 'pass'
                'lasttaskresult' = 'pass'; 'setboot-round-trip' = 'pass'; 'setboot-restored' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{
                'setup' = 'pass'; 'ace-present' = 'pass'; 'runex' = 'pass'; 'arguments-arrive' = 'pass'
                'lasttaskresult' = 'pass'; 'setboot-round-trip' = 'pass'; 'setboot-restored' = 'pass' } }
    }

    # ------------------------------------------------- 08 acceptance power cycle
    '08-acceptance-power-cycle|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'default-config' = 'pass'; 'blocked-before-power-cycle' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'default-config' = 'pass'; 'blocked-before-power-cycle' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'default-config' = 'pass'; 'blocked-before-power-cycle' = 'pass' } }
    }
    # This is the half the defect used to destroy. The throw was on the "connected at
    # start-up" count, and it took the whole left-click half with it: left-click-connects,
    # no-admin-prompt, left-click-disconnects, no-admin-prompt-disconnect and
    # blocked-again-after-click, which are definition-of-done item 2. All five are named here,
    # in all three columns, so the run has to reach them whatever the log holds. clean-exit is
    # the one this half counts: no clean stop line in the log leaves it inconclusive.
    '08-acceptance-power-cycle|resume' = @{
        none = @{ Overall = 'inconclusive'; Criteria = @{
                'ACCEPTANCE' = 'pass'; 'still-blocked' = 'pass'; 'no-fresh-handsfree-node' = 'pass'; 'not-taken-by-pc' = 'pass'
                'left-click-connects' = 'pass'; 'no-admin-prompt' = 'pass'; 'click-agrees-with-endpoints' = 'pass'
                'left-click-disconnects' = 'pass'; 'no-admin-prompt-disconnect' = 'pass'
                'disconnect-agrees-with-endpoints' = 'pass'; 'blocked-again-after-click' = 'pass'
                'icon-clean' = 'pass'; 'icon-dpi' = 'pass'; 'icon-theme' = 'pass'; 'card-no-focus' = 'pass'
                'click-once' = 'pass'; 'menu' = 'pass'; 'startup-value' = 'any'; 'startup-agrees' = 'pass'
                'single-instance' = 'pass'; 'clean-exit' = 'inconclusive' } }
        one  = @{ Overall = @('pass', 'inconclusive'); Criteria = @{
                'ACCEPTANCE' = 'pass'; 'still-blocked' = 'pass'; 'no-fresh-handsfree-node' = 'pass'; 'not-taken-by-pc' = 'pass'
                'left-click-connects' = 'pass'; 'no-admin-prompt' = 'pass'; 'click-agrees-with-endpoints' = 'pass'
                'left-click-disconnects' = 'pass'; 'no-admin-prompt-disconnect' = 'pass'
                'disconnect-agrees-with-endpoints' = 'pass'; 'blocked-again-after-click' = 'pass'
                'icon-clean' = 'pass'; 'icon-dpi' = 'pass'; 'icon-theme' = 'pass'; 'card-no-focus' = 'pass'
                'click-once' = 'pass'; 'menu' = 'pass'; 'startup-value' = 'any'; 'startup-agrees' = 'pass'
                'single-instance' = 'pass'; 'clean-exit' = 'pass' } }
        two  = @{ Overall = @('pass', 'inconclusive'); Criteria = @{
                'ACCEPTANCE' = 'pass'; 'still-blocked' = 'pass'; 'no-fresh-handsfree-node' = 'pass'; 'not-taken-by-pc' = 'pass'
                'left-click-connects' = 'pass'; 'no-admin-prompt' = 'pass'; 'click-agrees-with-endpoints' = 'pass'
                'left-click-disconnects' = 'pass'; 'no-admin-prompt-disconnect' = 'pass'
                'disconnect-agrees-with-endpoints' = 'pass'; 'blocked-again-after-click' = 'pass'
                'icon-clean' = 'pass'; 'icon-dpi' = 'pass'; 'icon-theme' = 'pass'; 'card-no-focus' = 'pass'
                'click-once' = 'pass'; 'menu' = 'pass'; 'startup-value' = 'any'; 'startup-agrees' = 'pass'
                'single-instance' = 'pass'; 'clean-exit' = 'pass' } }
    }

    # --------------------------------------------- 09 shutdown while connected
    # This half passes a reason to Complete-LiveTestRun once the owner is ready: shutting down
    # with the nodes still enabled is the point of it, so the at-rest closing step never offers a
    # block once that reason is set. none/one/two are also pinned by the at-rest default table
    # (Invoke-SelfTest.ps1); the FindingsInclude/Steps below are kept alongside it for the same
    # values, spelling out case (d) at this row specifically: no step offered, the reason
    # printed, leftAtRest = no-on-purpose.
    '09-shutdown-while-connected|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'no-on-purpose' }
            Steps = @{ 'diag gate block' = 0 } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'no-on-purpose' }
            Steps = @{ 'diag gate block' = 0 } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'no-on-purpose' }
            Steps = @{ 'diag gate block' = 0 } }
        # The owner says no to "ready to start": nothing runs, nothing shuts down, and the reason
        # (set only once $ready is true) must never have been set either. The closing check still
        # reads the nodes fresh (StartState Allowed, untouched by anything this half did), offers
        # and blocks them, exactly as any other run that never got going would.
        'declined-start' = @{ Overall = 'inconclusive'; Criteria = @{}
            FindingsInclude = @{ 'leftAtRest' = 'yes' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 0 } }
    }
    # end-session-logged counts the query and end lines, so it is a fail when the log holds
    # neither. That is the answer, and the run still reaches the two criteria after it.
    # declined-start only changes the first half; this half runs the same as none either way.
    '09-shutdown-while-connected|resume' = @{
        none = @{ Overall = 'fail'; Criteria = @{ 'not-paged-at-boot' = 'pass'; 'nodes-after-boot' = 'pass'; 'end-session-logged' = 'fail' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'not-paged-at-boot' = 'pass'; 'nodes-after-boot' = 'pass'; 'end-session-logged' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'not-paged-at-boot' = 'pass'; 'nodes-after-boot' = 'pass'; 'end-session-logged' = 'pass' } }
        'declined-start' = @{ Overall = 'fail'; Criteria = @{ 'not-paged-at-boot' = 'pass'; 'nodes-after-boot' = 'pass'; 'end-session-logged' = 'fail' } }
    }

    # ------------------------------------------------------- 10 shutdown messages
    # The first half records no criterion at all: it reads the state, writes the marker and
    # prints the resume line. A run with nothing recorded is inconclusive, which is right.
    '10-shutdown-messages-v1|first' = @{
        none = @{ Overall = 'inconclusive'; Criteria = @{ } }
        one  = @{ Overall = 'inconclusive'; Criteria = @{ } }
        two  = @{ Overall = 'inconclusive'; Criteria = @{ } }
    }
    # Every criterion in this half is a count out of the log, taken from the marker time the
    # first half wrote, so all three move together.
    '10-shutdown-messages-v1|resume' = @{
        none = @{ Overall = 'fail'; Criteria = @{ 'query-arrived' = 'fail'; 'end-arrived' = 'fail'; 'block-queued' = 'inconclusive' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'query-arrived' = 'pass'; 'end-arrived' = 'pass'; 'block-queued' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'query-arrived' = 'pass'; 'end-arrived' = 'pass'; 'block-queued' = 'pass' } }
    }

    # ------------------------------------------------------ 11 battery disconnected
    # The two battery criteria pass when nothing came back, so this is the one test whose
    # empty case is the good one. leg1-preconditions is not recorded: the fake AirPods start
    # off this PC, which is what leg 1 needs.
    '11-battery-disconnected|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'sweep-disconnected' = 'pass'; 'no-battery-disconnected' = 'pass'; 'sweep-connected' = 'pass' } }
        one  = @{ Overall = 'fail'; Criteria = @{ 'sweep-disconnected' = 'pass'; 'no-battery-disconnected' = 'fail'; 'sweep-connected' = 'fail' } }
        two  = @{ Overall = 'fail'; Criteria = @{ 'sweep-disconnected' = 'pass'; 'no-battery-disconnected' = 'fail'; 'sweep-connected' = 'fail' } }
    }

    # ---------------------------------------------------------- 12 callback thread
    '12-callback-thread|first' = @{
        none = @{ Overall = 'fail'; Criteria = @{ 'notifications-arrive' = 'fail'; 'apartment' = 'inconclusive'; 'protection-churn' = 'pass'; 'clean-exit' = 'inconclusive' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'notifications-arrive' = 'pass'; 'apartment' = 'pass'; 'protection-churn' = 'pass'; 'clean-exit' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'notifications-arrive' = 'pass'; 'apartment' = 'pass'; 'protection-churn' = 'pass'; 'clean-exit' = 'pass' } }
    }

    # ------------------------------------------------------------- 13 grace window
    # All three criteria are named in every column: Findings is declared for this block, which
    # makes it exhaustive the same as Criteria, so every one of the five findings the script
    # always records (secondsFromIdleToBlock, idleBlockDeferrals, idleDelaySecondsAtBlock,
    # suggestedIdleGraceSeconds, idleRuleReArmed) is pinned in every case.
    #
    # blocks-when-idle is inconclusive, not pass, for "none": nothing blocked in the log, and the
    # nodes read Blocked only because that is where 13-grace-window|first starts (Fakes.psm1,
    # StartStates), never because the idle rule was seen to do it. Every block-derived finding is
    # $null there for the same reason: nothing was measured.
    #
    # For "one" and "two" the block line is in the log from the start (Fakes.psm1 stamps it an
    # hour ahead of now), so it is found on the first 15 s poll: secondsFromIdleToBlock is 15,
    # idleDelaySecondsAtBlock is 45 (the figure LogFixtures writes into that line, read back
    # rather than assumed), and suggestedIdleGraceSeconds equals it, because neither case's log
    # holds a failed-automatic-block line, so nothing rules the grace out.
    #
    # grace-doubled and grace-unparsable are 13's own cases (Fakes.psm1, New-FakeSandbox): the
    # first carries a failed-block line before a 90 s block line, so idleDelaySecondsAtBlock is
    # 90 but suggestedIdleGraceSeconds is $null (a doubling cannot be ruled out); the second
    # carries a block line with no parseable figure, so both are $null and not-too-long, which
    # has nothing to compare, is inconclusive rather than silently passing or failing.
    # 13 never sends "diag gate allow" or "diag gate block" itself, and its own start state is
    # already Blocked (Fakes.psm1, StartStates), so every case here also proves case (c) of the
    # at-rest closing step: the nodes already read Blocked, so nothing is offered, and
    # leftAtRest = yes without a single "diag gate block" step.
    '13-grace-window|first' = @{
        none = @{ Overall = 'inconclusive'; Criteria = @{ 'not-too-short' = 'pass'; 'blocks-when-idle' = 'inconclusive'; 'not-too-long' = 'inconclusive' }
            Findings = @{
                'secondsFromIdleToBlock' = $null; 'idleBlockDeferrals' = 0; 'idleDelaySecondsAtBlock' = $null
                'suggestedIdleGraceSeconds' = $null; 'idleRuleReArmed' = 0; 'leftAtRest' = 'yes'
            }
            Steps = @{ 'diag gate block' = 0; 'diag disconnect' = 0 } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'not-too-short' = 'pass'; 'blocks-when-idle' = 'pass'; 'not-too-long' = 'pass' }
            Findings = @{
                'secondsFromIdleToBlock' = 15; 'idleBlockDeferrals' = 1; 'idleDelaySecondsAtBlock' = 45
                'suggestedIdleGraceSeconds' = 45; 'idleRuleReArmed' = 1; 'leftAtRest' = 'yes'
            }
            Steps = @{ 'diag gate block' = 0; 'diag disconnect' = 0 } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'not-too-short' = 'pass'; 'blocks-when-idle' = 'pass'; 'not-too-long' = 'pass' }
            Findings = @{
                'secondsFromIdleToBlock' = 15; 'idleBlockDeferrals' = 2; 'idleDelaySecondsAtBlock' = 45
                'suggestedIdleGraceSeconds' = 45; 'idleRuleReArmed' = 2; 'leftAtRest' = 'yes'
            }
            Steps = @{ 'diag gate block' = 0; 'diag disconnect' = 0 } }
        'grace-doubled' = @{ Overall = 'pass'; Criteria = @{ 'not-too-short' = 'pass'; 'blocks-when-idle' = 'pass'; 'not-too-long' = 'pass' }
            Findings = @{
                'secondsFromIdleToBlock' = 15; 'idleBlockDeferrals' = 0; 'idleDelaySecondsAtBlock' = 90
                'suggestedIdleGraceSeconds' = $null; 'idleRuleReArmed' = 0; 'leftAtRest' = 'yes'
            }
            Steps = @{ 'diag gate block' = 0; 'diag disconnect' = 0 } }
        'grace-unparsable' = @{ Overall = 'inconclusive'; Criteria = @{ 'not-too-short' = 'pass'; 'blocks-when-idle' = 'pass'; 'not-too-long' = 'inconclusive' }
            Findings = @{
                'secondsFromIdleToBlock' = 15; 'idleBlockDeferrals' = 0; 'idleDelaySecondsAtBlock' = $null
                'suggestedIdleGraceSeconds' = $null; 'idleRuleReArmed' = 0; 'leftAtRest' = 'yes'
            }
            Steps = @{ 'diag gate block' = 0; 'diag disconnect' = 0 } }
    }

    # ------------------------------------------------------ 14 set-device refusal
    # refuses-protected-move counts the services protection.json records as turned off, so it
    # is inconclusive when the record is empty and there is nothing for the guard to protect.
    # phone-address is not recorded: an address was available, so that branch is not taken.
    '14-set-device-refusal|first' = @{
        none = @{ Overall = 'inconclusive'; Criteria = @{
                'refuses-phone' = 'pass'; 'device-file-unchanged' = 'pass'; 'phone-untouched' = 'pass'
                'refuses-protected-move' = 'inconclusive'; 'picker-lists-devices' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'refuses-phone' = 'pass'; 'device-file-unchanged' = 'pass'; 'phone-untouched' = 'pass'
                'refuses-protected-move' = 'pass'; 'picker-lists-devices' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{
                'refuses-phone' = 'pass'; 'device-file-unchanged' = 'pass'; 'phone-untouched' = 'pass'
                'refuses-protected-move' = 'pass'; 'picker-lists-devices' = 'pass' } }
    }

    # ------------------------------------------------------ 15 uninstall reversal
    # program-folder and delayed-deletion read Program Files, which no parent can redirect,
    # so only that they are recorded is proven here.
    '15-uninstall-reversal|first' = @{
        none = @{ Overall = @('pass', 'fail'); Criteria = @{
                'uninstall' = 'pass'; 'nodes-restored' = 'pass'; 'services-restored' = 'pass'; 'tasks-removed' = 'pass'
                'data-folder-removed' = 'pass'; 'program-folder' = 'any'; 'install-again' = 'pass' } }
        one  = @{ Overall = @('pass', 'fail'); Criteria = @{
                'uninstall' = 'pass'; 'nodes-restored' = 'pass'; 'services-restored' = 'pass'; 'tasks-removed' = 'pass'
                'data-folder-removed' = 'pass'; 'program-folder' = 'any'; 'install-again' = 'pass' } }
        two  = @{ Overall = @('pass', 'fail'); Criteria = @{
                'uninstall' = 'pass'; 'nodes-restored' = 'pass'; 'services-restored' = 'pass'; 'tasks-removed' = 'pass'
                'data-folder-removed' = 'pass'; 'program-folder' = 'any'; 'install-again' = 'pass' } }
    }
    '15-uninstall-reversal|resume' = @{
        none = @{ Overall = @('pass', 'fail'); Criteria = @{ 'delayed-deletion' = 'any' } }
        one  = @{ Overall = @('pass', 'fail'); Criteria = @{ 'delayed-deletion' = 'any' } }
        two  = @{ Overall = @('pass', 'fail'); Criteria = @{ 'delayed-deletion' = 'any' } }
    }

    # ------------------------------------------------------ 16 switching between the phone and this PC
    # The rounds are 3 by default, so there are four switches to this PC (three rounds and the one before the last legs),
    # three to the phone with the phone playing, and one with it idle: eight lines to read in all, and four to the
    # phone. The fixture click line took the allow-first path and the shortcut one the direct path, so the paths
    # differ. A line's figures are read back rather than assumed. The findings are a declared, exhaustive block.
    #
    # none: no switch line at all, so nothing was measured and the two criteria that need a line are inconclusive,
    # never a pass; the criteria that rest on the owner's answers and on this script's own reads still come out.
    # two: every switch has two lines with its trigger, so it is not clear which is which and nothing is measured.
    # switch-timed-out: the one switch line is a "not active", so the timeout criterion fails on a line that is there.
    # switch-not-at-rest: the nodes read Allowed after every switch to the phone, so the at-rest criterion fails on
    # this script's own read, and the closing step is what blocks them (one "diag gate block"). switch-rejected: the
    # owner said no to every wait, blind, so both acceptable criteria fail and the smallest figure said no to is kept.
    # switch-unparsable: every line is there with its figure mangled, so nothing is measured and nothing reads as 0.
    '16-fast-switch|first' = @{
        none = @{ Overall = 'inconclusive'; Criteria = @{
                'preconditions' = 'pass'; 'to-pc-measured' = 'inconclusive'; 'to-pc-no-timeout' = 'inconclusive'; 'to-pc-heard' = 'pass'
                'to-pc-acceptable' = 'pass'; 'to-phone-measured' = 'inconclusive'; 'to-phone-at-rest' = 'pass'; 'to-phone-taken-back' = 'pass'
                'to-phone-acceptable' = 'pass'; 'shortcut-route' = 'inconclusive'; 'click-route' = 'inconclusive'; 'protection-held' = 'pass' }
            Findings = @{
                'toPcActiveMs' = $null; 'toPcMinMs' = $null; 'toPcMedianMs' = $null; 'toPcMaxMs' = $null; 'toPcSamples' = $null
                'toPcPaths' = $null; 'toPcPhases' = $null; 'protectionRanOnSwitch' = 'unknown unknown unknown unknown'
                'soundCutOutAfterStart' = 'no'; 'toPhoneReleasedMs' = $null; 'toPhoneAtRestMs' = $null
                'phoneHeardAfterReleaseMsOwnerObserved' = $null; 'phoneTakesThemBackWhenIdle' = 'yes'
                'toPcAcceptedUpToMs' = $null; 'toPcRejectedFromMs' = $null; 'toPhoneAcceptedUpToMs' = $null; 'toPhoneRejectedFromMs' = $null
                'directPathActiveMs' = $null; 'contendedSwitchesExcluded' = $null; 'firstRealPressOfShortcuts' = 'yes'; 'leftAtRest' = 'yes'
            }
            Steps = @{ 'diag gate block' = 0; 'diag disconnect' = 0 } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'preconditions' = 'pass'; 'to-pc-measured' = 'pass'; 'to-pc-no-timeout' = 'pass'; 'to-pc-heard' = 'pass'
                'to-pc-acceptable' = 'pass'; 'to-phone-measured' = 'pass'; 'to-phone-at-rest' = 'pass'; 'to-phone-taken-back' = 'pass'
                'to-phone-acceptable' = 'pass'; 'shortcut-route' = 'pass'; 'click-route' = 'pass'; 'protection-held' = 'pass' }
            Findings = @{
                'toPcActiveMs' = '1719 2749 2749 2749'; 'toPcMinMs' = 1719; 'toPcMedianMs' = 2749; 'toPcMaxMs' = 2749; 'toPcSamples' = 4
                'toPcPaths' = 'allow-first direct direct direct'
                'toPcPhases' = 'queued 0, first-pass 12, status 6, allow 251, endpoints 9, connect 1441, protection 3228 queued 0, first-pass 2749, status -, allow -, endpoints -, connect -, protection 3300 queued 0, first-pass 2749, status -, allow -, endpoints -, connect -, protection 3300 queued 0, first-pass 2749, status -, allow -, endpoints -, connect -, protection 3300'
                'protectionRanOnSwitch' = 'yes yes yes yes'; 'soundCutOutAfterStart' = 'no'
                'toPhoneReleasedMs' = '9 58 58 58'; 'toPhoneAtRestMs' = '291 349 349 349'
                'phoneHeardAfterReleaseMsOwnerObserved' = $null; 'phoneTakesThemBackWhenIdle' = 'yes'
                'toPcAcceptedUpToMs' = 2749; 'toPcRejectedFromMs' = $null; 'toPhoneAcceptedUpToMs' = 58; 'toPhoneRejectedFromMs' = $null
                'directPathActiveMs' = 2749; 'contendedSwitchesExcluded' = $null; 'firstRealPressOfShortcuts' = 'yes'; 'leftAtRest' = 'yes'
            }
            Steps = @{ 'diag gate block' = 0; 'diag disconnect' = 0 } }
        two  = @{ Overall = 'inconclusive'; Criteria = @{
                'preconditions' = 'pass'; 'to-pc-measured' = 'inconclusive'; 'to-pc-no-timeout' = 'pass'; 'to-pc-heard' = 'pass'
                'to-pc-acceptable' = 'pass'; 'to-phone-measured' = 'inconclusive'; 'to-phone-at-rest' = 'pass'; 'to-phone-taken-back' = 'pass'
                'to-phone-acceptable' = 'pass'; 'shortcut-route' = 'pass'; 'click-route' = 'pass'; 'protection-held' = 'pass' }
            Findings = @{
                'toPcActiveMs' = $null; 'toPcMinMs' = $null; 'toPcMedianMs' = $null; 'toPcMaxMs' = $null; 'toPcSamples' = $null
                'toPcPaths' = $null; 'toPcPhases' = $null; 'protectionRanOnSwitch' = 'unknown unknown unknown unknown'
                'soundCutOutAfterStart' = 'no'; 'toPhoneReleasedMs' = $null; 'toPhoneAtRestMs' = $null
                'phoneHeardAfterReleaseMsOwnerObserved' = $null; 'phoneTakesThemBackWhenIdle' = 'yes'
                'toPcAcceptedUpToMs' = $null; 'toPcRejectedFromMs' = $null; 'toPhoneAcceptedUpToMs' = $null; 'toPhoneRejectedFromMs' = $null
                'directPathActiveMs' = $null; 'contendedSwitchesExcluded' = $null; 'firstRealPressOfShortcuts' = 'yes'; 'leftAtRest' = 'yes'
            }
            Steps = @{ 'diag gate block' = 0; 'diag disconnect' = 0 } }
        'switch-timed-out' = @{ Overall = 'fail'; Criteria = @{
                'preconditions' = 'pass'; 'to-pc-measured' = 'inconclusive'; 'to-pc-no-timeout' = 'fail'; 'to-pc-heard' = 'pass'
                'to-pc-acceptable' = 'pass'; 'to-phone-measured' = 'inconclusive'; 'to-phone-at-rest' = 'pass'; 'to-phone-taken-back' = 'pass'
                'to-phone-acceptable' = 'pass'; 'shortcut-route' = 'fail'; 'click-route' = 'fail'; 'protection-held' = 'pass' }
            FindingsInclude = @{ 'toPcActiveMs' = $null; 'toPcSamples' = $null }
            Steps = @{ 'diag gate block' = 0; 'diag disconnect' = 0 } }
        'switch-not-at-rest' = @{ Overall = 'fail'; Criteria = @{
                'preconditions' = 'pass'; 'to-pc-measured' = 'pass'; 'to-pc-no-timeout' = 'pass'; 'to-pc-heard' = 'pass'
                'to-pc-acceptable' = 'pass'; 'to-phone-measured' = 'pass'; 'to-phone-at-rest' = 'fail'; 'to-phone-taken-back' = 'pass'
                'to-phone-acceptable' = 'pass'; 'shortcut-route' = 'pass'; 'click-route' = 'pass'; 'protection-held' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'yes' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 0 } }
        'switch-rejected' = @{ Overall = 'fail'; Criteria = @{
                'preconditions' = 'pass'; 'to-pc-measured' = 'pass'; 'to-pc-no-timeout' = 'pass'; 'to-pc-heard' = 'pass'
                'to-pc-acceptable' = 'fail'; 'to-phone-measured' = 'pass'; 'to-phone-at-rest' = 'pass'; 'to-phone-taken-back' = 'pass'
                'to-phone-acceptable' = 'fail'; 'shortcut-route' = 'pass'; 'click-route' = 'pass'; 'protection-held' = 'pass' }
            FindingsInclude = @{
                'toPcAcceptedUpToMs' = $null; 'toPcRejectedFromMs' = 1719; 'toPhoneAcceptedUpToMs' = $null; 'toPhoneRejectedFromMs' = 9
            }
            Steps = @{ 'diag gate block' = 0; 'diag disconnect' = 0 } }
        'switch-unparsable' = @{ Overall = 'inconclusive'; Criteria = @{
                'preconditions' = 'pass'; 'to-pc-measured' = 'inconclusive'; 'to-pc-no-timeout' = 'inconclusive'; 'to-pc-heard' = 'pass'
                'to-pc-acceptable' = 'pass'; 'to-phone-measured' = 'inconclusive'; 'to-phone-at-rest' = 'pass'; 'to-phone-taken-back' = 'pass'
                'to-phone-acceptable' = 'pass'; 'shortcut-route' = 'pass'; 'click-route' = 'pass'; 'protection-held' = 'pass' }
            Findings = @{
                'toPcActiveMs' = $null; 'toPcMinMs' = $null; 'toPcMedianMs' = $null; 'toPcMaxMs' = $null; 'toPcSamples' = $null
                'toPcPaths' = $null; 'toPcPhases' = $null; 'protectionRanOnSwitch' = 'unknown unknown unknown unknown'
                'soundCutOutAfterStart' = 'no'; 'toPhoneReleasedMs' = $null; 'toPhoneAtRestMs' = $null
                'phoneHeardAfterReleaseMsOwnerObserved' = $null; 'phoneTakesThemBackWhenIdle' = 'yes'
                'toPcAcceptedUpToMs' = $null; 'toPcRejectedFromMs' = $null; 'toPhoneAcceptedUpToMs' = $null; 'toPhoneRejectedFromMs' = $null
                'directPathActiveMs' = $null; 'contendedSwitchesExcluded' = $null; 'firstRealPressOfShortcuts' = 'yes'; 'leftAtRest' = 'yes'
            }
            Steps = @{ 'diag gate block' = 0; 'diag disconnect' = 0 } }
    }

    # ------------------------------------------------------ 17 hand back on shut down
    '17-handback-on-shutdown|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass' } }
        'declined-start' = @{ Overall = 'inconclusive'; Criteria = @{} }
        'handback-cut-short' = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass' } }
        'handback-not-reached' = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass' } }
        'service-not-run' = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass' } }
    }
    '17-handback-on-shutdown|resume' = @{
        none = @{ Overall = 'fail'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'end-session-logged' = 'fail'; 'handback-started' = 'fail'; 'handback-disconnect-confirmed' = 'inconclusive'
                'handback-block-sent' = 'fail'; 'handback-finished' = 'fail'; 'shutdown-was-clean' = 'fail'; 'service-ran' = 'fail' } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'end-session-logged' = 'pass'; 'handback-started' = 'pass'; 'handback-disconnect-confirmed' = 'pass'
                'handback-block-sent' = 'pass'; 'handback-finished' = 'pass'; 'shutdown-was-clean' = 'pass'; 'service-ran' = 'pass' }
            FindingsInclude = @{ 'handBackFinishedMs' = 303; 'handBackDisconnectMs' = 37; 'serviceReason' = 'already blocked'
                'servicePreshutdownMs' = 250; 'serviceResult' = 'success'; 'serviceVetoSeen' = 'no' } }
        two  = @{ Overall = 'fail'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'end-session-logged' = 'pass'; 'handback-started' = 'fail'; 'handback-disconnect-confirmed' = 'pass'
                'handback-block-sent' = 'pass'; 'handback-finished' = 'pass'; 'shutdown-was-clean' = 'pass'; 'service-ran' = 'pass' } }
        'declined-start' = @{ Overall = 'fail'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'end-session-logged' = 'fail'; 'handback-started' = 'fail'; 'handback-disconnect-confirmed' = 'inconclusive'
                'handback-block-sent' = 'fail'; 'handback-finished' = 'fail'; 'shutdown-was-clean' = 'fail'; 'service-ran' = 'inconclusive' }
            FindingsInclude = @{ 'serviceReason' = $null; 'servicePreshutdownMs' = $null; 'serviceResult' = $null; 'serviceVetoSeen' = 'no-evidence' } }
        'handback-cut-short' = @{ Overall = 'fail'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'end-session-logged' = 'pass'; 'handback-started' = 'pass'; 'handback-disconnect-confirmed' = 'pass'
                'handback-block-sent' = 'pass'; 'handback-finished' = 'fail'; 'shutdown-was-clean' = 'pass'; 'service-ran' = 'fail' }
            FindingsInclude = @{ 'handBackCutShortStillRunning' = 'block' } }
        'handback-not-reached' = @{ Overall = 'fail'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'end-session-logged' = 'fail'; 'handback-started' = 'fail'; 'handback-disconnect-confirmed' = 'inconclusive'
                'handback-block-sent' = 'fail'; 'handback-finished' = 'fail'; 'shutdown-was-clean' = 'fail'; 'service-ran' = 'fail' }
            FindingsInclude = @{ 'handBackFinishedMs' = $null; 'handBackDisconnectMs' = $null } }
        'service-not-run' = @{ Overall = 'fail'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'end-session-logged' = 'pass'; 'handback-started' = 'pass'; 'handback-disconnect-confirmed' = 'pass'
                'handback-block-sent' = 'pass'; 'handback-finished' = 'pass'; 'shutdown-was-clean' = 'pass'; 'service-ran' = 'fail' }
            FindingsInclude = @{ 'serviceReason' = $null; 'servicePreshutdownMs' = $null; 'serviceResult' = $null; 'serviceVetoSeen' = 'no-evidence' } }
    }

    # ------------------------------------------------------ 18 hand back on sleep
    '18-handback-on-sleep|first' = @{
        none = @{ Overall = 'fail'; Criteria = @{
                'sleep-happened' = 'inconclusive'; 'suspend-logged' = 'fail'; 'handback-started' = 'fail'
                'handback-disconnect-confirmed' = 'inconclusive'; 'handback-block-sent' = 'fail'; 'handback-finished-or-sent' = 'fail'
                'resume-logged' = 'fail'; 'nodes-after-wake' = 'pass'; 'not-repaged-at-wake' = 'pass'; 'heard-handed-back' = 'pass' }
            FindingsInclude = @{ 'firstSleepWithEarshotInstalled' = 'yes' } }
        one  = @{ Overall = 'inconclusive'; Criteria = @{
                'sleep-happened' = 'inconclusive'; 'suspend-logged' = 'pass'; 'handback-started' = 'pass'
                'handback-disconnect-confirmed' = 'pass'; 'handback-block-sent' = 'pass'; 'handback-finished-or-sent' = 'pass'
                'resume-logged' = 'pass'; 'nodes-after-wake' = 'pass'; 'not-repaged-at-wake' = 'pass'; 'heard-handed-back' = 'pass' }
            FindingsInclude = @{ 'handBackFinishedMs' = 280; 'handBackDisconnectMs' = 22; 'firstSleepWithEarshotInstalled' = 'yes' } }
        two  = @{ Overall = 'fail'; Criteria = @{
                'sleep-happened' = 'inconclusive'; 'suspend-logged' = 'fail'; 'handback-started' = 'pass'
                'handback-disconnect-confirmed' = 'pass'; 'handback-block-sent' = 'pass'; 'handback-finished-or-sent' = 'pass'
                'resume-logged' = 'pass'; 'nodes-after-wake' = 'pass'; 'not-repaged-at-wake' = 'pass'; 'heard-handed-back' = 'pass' }
            FindingsInclude = @{ 'firstSleepWithEarshotInstalled' = 'yes' } }
        'no-sleep-event' = @{ Overall = 'inconclusive'; Criteria = @{
                'sleep-happened' = 'inconclusive'; 'suspend-logged' = 'pass'; 'handback-started' = 'pass'
                'handback-disconnect-confirmed' = 'pass'; 'handback-block-sent' = 'pass'; 'handback-finished-or-sent' = 'pass'
                'resume-logged' = 'pass'; 'nodes-after-wake' = 'pass'; 'not-repaged-at-wake' = 'pass'; 'heard-handed-back' = 'pass' }
            FindingsInclude = @{ 'firstSleepWithEarshotInstalled' = 'yes' } }
        'handback-cut-short' = @{ Overall = 'fail'; Criteria = @{
                'sleep-happened' = 'pass'; 'suspend-logged' = 'fail'; 'handback-started' = 'pass'
                'handback-disconnect-confirmed' = 'fail'; 'handback-block-sent' = 'fail'; 'handback-finished-or-sent' = 'fail'
                'resume-logged' = 'fail'; 'nodes-after-wake' = 'pass'; 'not-repaged-at-wake' = 'pass'; 'heard-handed-back' = 'pass' }
            FindingsInclude = @{ 'firstSleepWithEarshotInstalled' = 'yes' } }
        # The render endpoint reads ACTIVE again once the owner is back (this computer re-paged the
        # AirPods, the point of this case), so the resume check never got the chance to re-block:
        # the nodes read Allowed, not Blocked, at the close. The closing step's own re-read is what
        # has to offer the block, and it renders too, so the disconnect-first fix runs here as well.
        'repaged-at-wake' = @{ Overall = 'fail'; Criteria = @{
                'sleep-happened' = 'pass'; 'suspend-logged' = 'pass'; 'handback-started' = 'pass'
                'handback-disconnect-confirmed' = 'pass'; 'handback-block-sent' = 'pass'; 'handback-finished-or-sent' = 'pass'
                'resume-logged' = 'pass'; 'nodes-after-wake' = 'fail'; 'not-repaged-at-wake' = 'fail'; 'heard-handed-back' = 'pass' }
            FindingsInclude = @{ 'leftAtRest' = 'yes'; 'firstSleepWithEarshotInstalled' = 'yes' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 1 } }
    }

    # ------------------------------------------------------ 19 the widget
    #
    # notification-shortcut-exists is 'fail' in every case: the sandbox redirects APPDATA under
    # itself, and nothing here writes Earshot.lnk into it, so the real Test-Path this criterion
    # runs is honestly answering "not found" on a machine where that is correct, exactly as it
    # would on a real machine Earshot has never started on. battery-setup-reachable, battery-setup-flow
    # and battery-setup-honest-result are always 'pass': the fake owner answers 'yes' to every set-up
    # question (the item is there and available, the card went through its three steps, and showed a
    # figure only for what the set-up proved). low-battery-alert-fires stays 'inconclusive' in every
    # case: the alert can never be fed a proved reading by this test, whatever the case. explorer-restart-reattach and
    # positive-control-counters need at least one matching log line to pass; case-open-no-auto-
    # connect needs zero, so it is the one criterion that flips the other way as the case grows.
    # atRest: render never reads Active at the close (the last owner action before it is the
    # case-open step, which touches neither "connect" nor "left-click"), so the closing step never
    # offers a disconnect, only the block; nodes start Allowed and end Blocked in every case.
    '19-widget|first' = @{
        none = @{ Overall = 'fail'; Criteria = @{
                'gauge-placement' = 'pass'; 'gauge-follows-buttons' = 'pass'; 'gauge-follows-alignment' = 'pass'
                'gauge-follows-autohide' = 'pass'; 'gauge-hidden-fullscreen' = 'pass'; 'explorer-restart-reattach' = 'fail'
                'gauge-card-dpi' = 'pass'; 'gauge-card-theme' = 'pass'; 'gauge-click-behaviour' = 'pass'
                'positive-control-counters' = 'inconclusive'; 'battery-setup-reachable' = 'pass'
                'battery-honestly-not-shown' = 'pass'; 'where-this-pc' = 'pass'; 'auto-pause-inert' = 'pass'
                'where-not-connected-honest' = 'pass'; 'case-open-card-honestly-not-shown' = 'pass'
                'case-open-no-auto-connect' = 'pass'; 'bluetooth-off-then-on' = 'inconclusive'
                'notification-shortcut-exists' = 'fail'; 'low-battery-alert-fires' = 'inconclusive'
                'battery-setup-flow' = 'pass'; 'battery-setup-honest-result' = 'pass'
                'gauge-next-to-apps' = 'pass'; 'gauge-ring-accent' = 'pass'; 'gauge-reading-older-than-an-hour' = 'pass'
                'gauge-stays-on-top' = 'inconclusive' }
            FindingsInclude = @{
                'claimFileExistsAtStart' = 'no'; 'batterySetupInMenu' = 'yes'; 'claimFileExistsAfterCheck' = 'no'
                'widgetAllSectionsSeen' = $null; 'widgetAppleSectionsSeen' = $null; 'widgetProximityItemsSeen' = $null
                'caseOpenToggleLinesSeen' = 0; 'widgetWatcherStoppedLine' = $null; 'leftAtRest' = 'yes'
                'gaugeRaisedLines' = 0; 'gaugeLeftUnderLines' = 0; 'gaugeRingShown' = 'yes' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 0 } }
        one  = @{ Overall = 'fail'; Criteria = @{
                'gauge-placement' = 'pass'; 'gauge-follows-buttons' = 'pass'; 'gauge-follows-alignment' = 'pass'
                'gauge-follows-autohide' = 'pass'; 'gauge-hidden-fullscreen' = 'pass'; 'explorer-restart-reattach' = 'pass'
                'gauge-card-dpi' = 'pass'; 'gauge-card-theme' = 'pass'; 'gauge-click-behaviour' = 'pass'
                'positive-control-counters' = 'pass'; 'battery-setup-reachable' = 'pass'
                'battery-honestly-not-shown' = 'pass'; 'where-this-pc' = 'pass'; 'auto-pause-inert' = 'pass'
                'where-not-connected-honest' = 'pass'; 'case-open-card-honestly-not-shown' = 'pass'
                'case-open-no-auto-connect' = 'fail'; 'bluetooth-off-then-on' = 'pass'
                'notification-shortcut-exists' = 'fail'; 'low-battery-alert-fires' = 'inconclusive'
                'battery-setup-flow' = 'pass'; 'battery-setup-honest-result' = 'pass'
                'gauge-next-to-apps' = 'pass'; 'gauge-ring-accent' = 'pass'; 'gauge-reading-older-than-an-hour' = 'pass'
                'gauge-stays-on-top' = 'pass' }
            FindingsInclude = @{
                'claimFileExistsAtStart' = 'no'; 'batterySetupInMenu' = 'yes'; 'claimFileExistsAfterCheck' = 'no'
                'widgetAllSectionsSeen' = 4; 'widgetAppleSectionsSeen' = 4; 'widgetProximityItemsSeen' = 4
                'caseOpenToggleLinesSeen' = 1; 'widgetWatcherStoppedLine' = 'any'; 'leftAtRest' = 'yes'
                'gaugeRaisedLines' = 1; 'gaugeLeftUnderLines' = 1; 'gaugeRingShown' = 'yes' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 0 } }
        two  = @{ Overall = 'fail'; Criteria = @{
                'gauge-placement' = 'pass'; 'gauge-follows-buttons' = 'pass'; 'gauge-follows-alignment' = 'pass'
                'gauge-follows-autohide' = 'pass'; 'gauge-hidden-fullscreen' = 'pass'; 'explorer-restart-reattach' = 'pass'
                'gauge-card-dpi' = 'pass'; 'gauge-card-theme' = 'pass'; 'gauge-click-behaviour' = 'pass'
                'positive-control-counters' = 'pass'; 'battery-setup-reachable' = 'pass'
                'battery-honestly-not-shown' = 'pass'; 'where-this-pc' = 'pass'; 'auto-pause-inert' = 'pass'
                'where-not-connected-honest' = 'pass'; 'case-open-card-honestly-not-shown' = 'pass'
                'case-open-no-auto-connect' = 'fail'; 'bluetooth-off-then-on' = 'pass'
                'notification-shortcut-exists' = 'fail'; 'low-battery-alert-fires' = 'inconclusive'
                'battery-setup-flow' = 'pass'; 'battery-setup-honest-result' = 'pass'
                'gauge-next-to-apps' = 'pass'; 'gauge-ring-accent' = 'pass'; 'gauge-reading-older-than-an-hour' = 'pass'
                'gauge-stays-on-top' = 'pass' }
            FindingsInclude = @{
                'claimFileExistsAtStart' = 'no'; 'batterySetupInMenu' = 'yes'; 'claimFileExistsAfterCheck' = 'no'
                'widgetAllSectionsSeen' = 4; 'widgetAppleSectionsSeen' = 4; 'widgetProximityItemsSeen' = 4
                'caseOpenToggleLinesSeen' = 2; 'widgetWatcherStoppedLine' = 'any'; 'leftAtRest' = 'yes'
                'gaugeRaisedLines' = 2; 'gaugeLeftUnderLines' = 2; 'gaugeRingShown' = 'yes' }
            Steps = @{ 'diag gate block' = 1; 'diag disconnect' = 0 } }
    }

    # ------------------------------------------------------ 20 hand back on Exit
    # The fake owner chooses Exit, and the world moves the way the real hand-back leaves it (render Unplugged, the nodes
    # Blocked), so the two state criteria pass in every case: what differs is what the log says. With no hand-back in the
    # log, "nothing said" proves nothing and is inconclusive, never a pass.
    '20-handback-on-exit|first' = @{
        none = @{ Overall = 'fail'; Criteria = @{
                'connected-first' = 'pass'; 'hand-back-on' = 'pass'; 'nodes-after-exit' = 'pass'; 'released-after-exit' = 'pass'
                'heard-handed-back' = 'pass'; 'exit-handback-started' = 'fail'; 'exit-disconnect-confirmed' = 'inconclusive'
                'exit-block-sent' = 'fail'; 'exit-finished' = 'fail'; 'exit-nothing-said' = 'inconclusive'
                'exit-paused-first' = 'fail'; 'exit-closed' = 'inconclusive' }
            FindingsInclude = @{ 'exitHandBackFinishedMs' = $null; 'exitDisconnectMs' = $null; 'exitBlockSentAfterMs' = $null; 'exitCutShortStillRunning' = $null } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'connected-first' = 'pass'; 'hand-back-on' = 'pass'; 'nodes-after-exit' = 'pass'; 'released-after-exit' = 'pass'
                'heard-handed-back' = 'pass'; 'exit-handback-started' = 'pass'; 'exit-disconnect-confirmed' = 'pass'
                'exit-block-sent' = 'pass'; 'exit-finished' = 'pass'; 'exit-nothing-said' = 'pass'
                'exit-paused-first' = 'pass'; 'exit-closed' = 'pass' }
            FindingsInclude = @{ 'exitHandBackFinishedMs' = 312; 'exitDisconnectMs' = 31; 'exitBlockSentAfterMs' = 350; 'exitCutShortStillRunning' = $null; 'pauseSettingAtExit' = 'on' } }
        two  = @{ Overall = 'fail'; Criteria = @{
                'connected-first' = 'pass'; 'hand-back-on' = 'pass'; 'nodes-after-exit' = 'pass'; 'released-after-exit' = 'pass'
                'heard-handed-back' = 'pass'; 'exit-handback-started' = 'fail'; 'exit-disconnect-confirmed' = 'pass'
                'exit-block-sent' = 'pass'; 'exit-finished' = 'pass'; 'exit-nothing-said' = 'pass'
                'exit-paused-first' = 'pass'; 'exit-closed' = 'pass' } }
        'exit-cut-short' = @{ Overall = 'fail'; Criteria = @{
                'connected-first' = 'pass'; 'hand-back-on' = 'pass'; 'nodes-after-exit' = 'pass'; 'released-after-exit' = 'pass'
                'heard-handed-back' = 'pass'; 'exit-handback-started' = 'pass'; 'exit-disconnect-confirmed' = 'pass'
                'exit-block-sent' = 'pass'; 'exit-finished' = 'fail'; 'exit-nothing-said' = 'fail'
                'exit-paused-first' = 'fail'; 'exit-closed' = 'inconclusive' }
            FindingsInclude = @{ 'exitCutShortStillRunning' = 'block'; 'exitHandBackFinishedMs' = $null } }
    }

    # ------------------------------------------------------ 21 pause when the AirPods leave this PC
    # The fake owner says the music paused, nothing resumed and nothing changed by itself, so the owner's own account is
    # the same in every case: it is the log that differs, and a criterion that reads the log is what has to fail when the
    # owner and the log disagree. pause-declined holds only a "Not paused" decision, which shares its words with a pause.
    '21-pause-on-leave|first' = @{
        none = @{ Overall = 'fail'; Criteria = @{
                'setting-on' = 'pass'; 'connected-first' = 'pass'; 'own-disconnect-paused' = 'pass'; 'own-disconnect-not-jumped' = 'pass'
                'own-disconnect-logged' = 'fail'; 'phone-take-paused' = 'pass'; 'phone-take-not-resumed' = 'pass'
                'phone-take-logged' = 'fail'; 'idle-leave-not-paused' = 'fail'; 'idle-leave-untouched' = 'pass' }
            FindingsInclude = @{ 'ownPauseMs' = $null; 'phonePausedAfterChangeMs' = $null; 'phoneReadingAgeMs' = $null; 'idleLeaveDecision' = $null } }
        one  = @{ Overall = 'pass'; Criteria = @{
                'setting-on' = 'pass'; 'connected-first' = 'pass'; 'own-disconnect-paused' = 'pass'; 'own-disconnect-not-jumped' = 'pass'
                'own-disconnect-logged' = 'pass'; 'phone-take-paused' = 'pass'; 'phone-take-not-resumed' = 'pass'
                'phone-take-logged' = 'pass'; 'idle-leave-not-paused' = 'pass'; 'idle-leave-untouched' = 'pass' }
            FindingsInclude = @{ 'ownPauseMs' = 38; 'phonePausedAfterChangeMs' = 8; 'phoneReadingAgeMs' = 640 } }
        two  = @{ Overall = 'pass'; Criteria = @{
                'setting-on' = 'pass'; 'connected-first' = 'pass'; 'own-disconnect-paused' = 'pass'; 'own-disconnect-not-jumped' = 'pass'
                'own-disconnect-logged' = 'pass'; 'phone-take-paused' = 'pass'; 'phone-take-not-resumed' = 'pass'
                'phone-take-logged' = 'pass'; 'idle-leave-not-paused' = 'pass'; 'idle-leave-untouched' = 'pass' }
            FindingsInclude = @{ 'ownPauseMs' = 38; 'phonePausedAfterChangeMs' = 8; 'phoneReadingAgeMs' = 640 } }
        'pause-declined' = @{ Overall = 'fail'; Criteria = @{
                'setting-on' = 'pass'; 'connected-first' = 'pass'; 'own-disconnect-paused' = 'pass'; 'own-disconnect-not-jumped' = 'pass'
                'own-disconnect-logged' = 'fail'; 'phone-take-paused' = 'pass'; 'phone-take-not-resumed' = 'pass'
                'phone-take-logged' = 'fail'; 'idle-leave-not-paused' = 'pass'; 'idle-leave-untouched' = 'pass' }
            FindingsInclude = @{ 'phonePausedAfterChangeMs' = $null; 'idleLeaveDecision' = 'this PC was not playing to them (last reading: silent).' } }
    }

    # ------------------------------------------------------ 22 hand back by the service
    # The fake owner ends the Earshot task in Task Manager, so the tray is gone while the AirPods stay connected. The status
    # files the fake service left decide the second half: none, declined-start and service-not-run wrote none; one and two
    # wrote a good one; service-partial wrote one with a refused node (and the control manager recorded an error for the
    # service); service-over-budget wrote one whose own times say 9,500 ms.
    '22-service-handback|first' = @{
        none = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass'; 'tray-gone-still-connected' = 'pass' }
            FindingsInclude = @{ 'serviceStateAtShutdown' = 'running'; 'serviceWorkingSetBytes' = 'any'; 'test17Result' = 'not-run' } }
        one  = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass'; 'tray-gone-still-connected' = 'pass' }
            FindingsInclude = @{ 'serviceStateAtShutdown' = 'running'; 'serviceWorkingSetBytes' = 'any'; 'handBackMirrorAtShutdown' = 'True' } }
        two  = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass'; 'tray-gone-still-connected' = 'pass' } }
        'declined-start' = @{ Overall = 'inconclusive'; Criteria = @{} }
        'service-not-run' = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass'; 'tray-gone-still-connected' = 'pass' } }
        'service-partial' = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass'; 'tray-gone-still-connected' = 'pass' } }
        'service-over-budget' = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass'; 'tray-gone-still-connected' = 'pass' } }
        'tray-not-ended' = @{ Overall = 'fail'; Criteria = @{ 'connected-first' = 'pass'; 'tray-gone-still-connected' = 'fail' } }
        'service-block-sent' = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass'; 'tray-gone-still-connected' = 'pass' } }
        'service-already-blocked' = @{ Overall = 'pass'; Criteria = @{ 'connected-first' = 'pass'; 'tray-gone-still-connected' = 'pass' } }
        # The process list could not be read, so "the tray is gone" is not known and the criterion is inconclusive, never a pass.
        # The earlier test 17 result cannot be read and the service's process cannot be found: each is recorded as such, with
        # its error, and neither becomes a figure or "not run".
        'tray-unreadable' = @{ Overall = 'inconclusive'; Criteria = @{ 'connected-first' = 'pass'; 'tray-gone-still-connected' = 'inconclusive' }
            FindingsInclude = @{ 'test17Result' = 'unreadable'; 'serviceWorkingSetBytes' = $null }
            ExpectedErrors = 2 }
    }
    '22-service-handback|resume' = @{
        none = @{ Overall = 'fail'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'service-ran' = 'fail'; 'service-blocked' = 'inconclusive'; 'service-in-budget' = 'inconclusive'
                'shutdown-was-clean' = 'fail'; 'service-no-error-event' = 'pass' }
            FindingsInclude = @{ 'serviceResult' = $null; 'serviceVetoSeen' = 'no-evidence'; 'serviceRetryTook' = 'no-evidence'; 'serviceEventIds' = '7024'; 'serviceState' = 'running' } }
        # The shared one and two write a status file whose reason is "already blocked": the service did not block, so
        # service-blocked is inconclusive and the run is too, whatever else passes.
        one  = @{ Overall = 'inconclusive'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'service-ran' = 'pass'; 'service-blocked' = 'inconclusive'; 'service-in-budget' = 'pass'
                'shutdown-was-clean' = 'pass'; 'service-no-error-event' = 'pass' }
            FindingsInclude = @{ 'servicePreshutdownMs' = 250; 'serviceResult' = 'success'; 'serviceReason' = 'already blocked'; 'serviceVetoSeen' = 'no'
                'serviceRetryTook' = 'no-veto'; 'serviceEventIds' = '7024'; 'serviceState' = 'running'; 'restartedNotShutDown' = 'no'
                'serviceBlockSentAfterMs' = $null; 'serviceBlockMs' = $null } }
        two  = @{ Overall = 'inconclusive'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'service-ran' = 'pass'; 'service-blocked' = 'inconclusive'; 'service-in-budget' = 'pass'
                'shutdown-was-clean' = 'pass'; 'service-no-error-event' = 'pass' } }
        # No shutdown time (the first half never got there): no status file and no event is read, so every criterion that
        # depends on one is inconclusive, even though an earlier sitting's files are in the folder.
        'declined-start' = @{ Overall = 'inconclusive'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'service-ran' = 'inconclusive'; 'service-blocked' = 'inconclusive'; 'service-in-budget' = 'inconclusive'
                'shutdown-was-clean' = 'inconclusive'; 'service-no-error-event' = 'inconclusive' }
            FindingsInclude = @{ 'servicePreshutdownMs' = $null; 'serviceBlockSentAfterMs' = $null; 'serviceResult' = $null; 'serviceReason' = $null
                'serviceVetoSeen' = 'no-evidence'; 'serviceEventIds' = $null } }
        'service-not-run' = @{ Overall = 'fail'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'service-ran' = 'fail'; 'service-blocked' = 'inconclusive'; 'service-in-budget' = 'inconclusive'
                'shutdown-was-clean' = 'pass'; 'service-no-error-event' = 'pass' }
            FindingsInclude = @{ 'servicePreshutdownMs' = $null; 'serviceResult' = $null; 'serviceReason' = $null } }
        'service-partial' = @{ Overall = 'fail'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'service-ran' = 'pass'; 'service-blocked' = 'fail'; 'service-in-budget' = 'pass'
                'shutdown-was-clean' = 'pass'; 'service-no-error-event' = 'fail' }
            FindingsInclude = @{ 'serviceResult' = 'partial'; 'serviceVetoSeen' = 'yes'; 'serviceRetryTook' = 'no'; 'servicePreshutdownMs' = 250 } }
        'service-over-budget' = @{ Overall = 'fail'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'service-ran' = 'pass'; 'service-blocked' = 'pass'; 'service-in-budget' = 'fail'
                'shutdown-was-clean' = 'pass'; 'service-no-error-event' = 'pass' }
            FindingsInclude = @{ 'servicePreshutdownMs' = 9500 } }
        'tray-not-ended' = @{ Overall = 'inconclusive'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'service-ran' = 'pass'; 'service-blocked' = 'inconclusive'; 'service-in-budget' = 'pass'
                'shutdown-was-clean' = 'pass'; 'service-no-error-event' = 'pass' } }
        # The status file says the service sent the block, 120 ms in, and had finished 250 ms in: the one way to pass.
        'service-block-sent' = @{ Overall = 'pass'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'service-ran' = 'pass'; 'service-blocked' = 'pass'; 'service-in-budget' = 'pass'
                'shutdown-was-clean' = 'pass'; 'service-no-error-event' = 'pass' }
            FindingsInclude = @{ 'servicePreshutdownMs' = 250; 'serviceBlockSentAfterMs' = 120; 'serviceBlockMs' = 130; 'serviceResult' = 'success'
                'serviceReason' = 'any'; 'serviceVetoSeen' = 'no' } }
        # The status file says the nodes were already blocked: the service did not block them, so nothing is settled.
        'service-already-blocked' = @{ Overall = 'inconclusive'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'service-ran' = 'pass'; 'service-blocked' = 'inconclusive'; 'service-in-budget' = 'pass'
                'shutdown-was-clean' = 'pass'; 'service-no-error-event' = 'pass' }
            FindingsInclude = @{ 'serviceReason' = 'already blocked'; 'serviceBlockSentAfterMs' = $null; 'serviceBlockMs' = $null; 'serviceResult' = 'success' } }
        'tray-unreadable' = @{ Overall = 'inconclusive'; Criteria = @{
                'nodes-after-boot' = 'pass'; 'not-paged-at-boot' = 'pass'; 'heard-handed-back' = 'pass'
                'service-ran' = 'pass'; 'service-blocked' = 'inconclusive'; 'service-in-budget' = 'pass'
                'shutdown-was-clean' = 'pass'; 'service-no-error-event' = 'pass' }
            FindingsInclude = @{ 'test17Result' = 'unreadable' }
            ExpectedErrors = 1 }
    }
}
