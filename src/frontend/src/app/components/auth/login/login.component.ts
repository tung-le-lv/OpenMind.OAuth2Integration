import { Component, OnInit, NgZone, AfterViewInit } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { MatSnackBar } from '@angular/material/snack-bar';
import { AuthService } from '../../../services/auth.service';
import { environment } from '../../../../environments/environment';

declare const google: any;

@Component({
  selector: 'app-login',
  templateUrl: './login.component.html'
})
export class LoginComponent implements OnInit, AfterViewInit {
  loginForm: FormGroup;
  loading = false;
  googleLoading = false;

  constructor(
    private fb: FormBuilder,
    private authService: AuthService,
    private router: Router,
    private snackBar: MatSnackBar,
    private ngZone: NgZone
  ) {
    this.loginForm = this.fb.group({
      email: ['', [Validators.required, Validators.email]],
      password: ['', Validators.required]
    });
  }

  ngOnInit(): void {}

  ngAfterViewInit(): void {
    this.initializeGoogleSignIn();
  }

  private initializeGoogleSignIn(): void {
    if (typeof google !== 'undefined' && google.accounts) {
      google.accounts.id.initialize({
        client_id: environment.googleClientId,
        callback: (response: any) => this.handleGoogleSignIn(response)
      });
      
      google.accounts.id.renderButton(
        document.getElementById('google-signin-button'),
        { 
          theme: 'outline', 
          size: 'large', 
          width: '100%',
          text: 'signin_with',
          shape: 'rectangular'
        }
      );
    } else {
      // Retry after a short delay if Google library hasn't loaded yet
      setTimeout(() => this.initializeGoogleSignIn(), 100);
    }
  }

  private handleGoogleSignIn(response: any): void {
    this.ngZone.run(() => {
      this.googleLoading = true;
      this.authService.googleLogin(response.credential).subscribe({
        next: () => {
          this.router.navigate(['/dashboard']);
        },
        error: (error) => {
          this.googleLoading = false;
          this.snackBar.open(
            error.error?.message || 'Google login failed. Please try again.',
            'Close',
            { duration: 5000 }
          );
        }
      });
    });
  }

  onSubmit(): void {
    if (this.loginForm.valid) {
      this.loading = true;
      this.authService.login(this.loginForm.value).subscribe({
        next: () => {
          this.router.navigate(['/dashboard']);
        },
        error: (error) => {
          this.loading = false;
          this.snackBar.open(
            error.error?.message || 'Login failed. Please try again.', 
            'Close', 
            { duration: 5000 }
          );
        }
      });
    }
  }
}